using System.IO;
using System.Text;
using System.Threading.Channels;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// Runtime is the C# counterpart to the TypeScript AgentRuntime. It exposes two
/// channels – Inputs and Outputs – that mirror the asynchronous queues used in
/// the original implementation. Inputs receive InputEvents, Outputs surfaces
/// RuntimeEvents.
/// </summary>
public partial class Runtime
{
    internal readonly RuntimeOptions _options;
    
    private readonly Channel<InputEvent> _inputs;
    private readonly Channel<RuntimeEvent> _outputs;
    
    private readonly SemaphoreSlim _closeOnce = new(1, 1);
    private readonly CancellationTokenSource _closedCts = new();
    
    private readonly PlanManager _plan;
    private readonly OpenAIClient _client;
    private CommandExecutor? _executor;
    private readonly SemaphoreSlim _commandMu = new(1, 1);
    
    private readonly SemaphoreSlim _workMu = new(1, 1);
    private bool _working;
    
    private readonly ReaderWriterLockSlim _historyMu = new();
    private List<ChatMessage> _history;
    
    private readonly object _passLock = new();
    private int _passCount;
    
    private readonly string _agentName;
    
    private readonly ContextBudget _contextBudget;
    
    // logFileCloser holds a reference to the log file if one was opened,
    // so it can be closed when the runtime shuts down.
    private IDisposable? _logFileCloser;

    private Runtime(RuntimeOptions options, OpenAIClient client)
    {
        _options = options;
        _inputs = Channel.CreateBounded<InputEvent>(new BoundedChannelOptions(options.InputBuffer)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        _outputs = Channel.CreateBounded<RuntimeEvent>(new BoundedChannelOptions(options.OutputBuffer)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        _plan = new PlanManager();
        _client = client;
        
        var initialHistory = new List<ChatMessage>
        {
            new()
            {
                Role = MessageRole.System,
                Content = SystemPrompt.Build(options.SystemPromptAugment),
                Timestamp = DateTime.Now,
                Pass = 0
            }
        };
        _history = initialHistory;
        _agentName = "main";
        _contextBudget = new ContextBudget
        {
            MaxTokens = options.MaxContextTokens,
            CompactWhenPercent = options.CompactWhenPercent
        };
    }

    /// <summary>
    /// NewRuntime configures a new runtime with the provided options.
    /// </summary>
    public static Runtime NewRuntime(RuntimeOptions options)
    {
        options = options.WithDefaults();
        options.Validate();
        
        var httpTimeout = options.HttpTimeout;
        if (httpTimeout == TimeSpan.Zero)
        {
            httpTimeout = TimeSpan.FromSeconds(120);
        }
        
        var client = new OpenAIClient(
            options.ApiKey,
            options.Model,
            options.ReasoningEffort,
            options.ApiBaseUrl,
            options.Logger!,
            options.Metrics!,
            options.ApiRetryConfig,
            httpTimeout
        );
        
        var rt = new Runtime(options, client);
        
        // If logger was created from a file, extract and store the file handle for cleanup
        if (options.Logger is StdLogger stdLogger)
        {
            if (stdLogger.Writer is StreamWriter sw)
            {
                rt._logFileCloser = sw;
            }
        }
        
        var executor = new CommandExecutor(options.Logger!, options.Metrics!);
        RegisterBuiltinInternalCommands(rt, executor);
        rt._executor = executor;
        
        foreach (var kvp in options.InternalCommands)
        {
            executor.RegisterInternalCommand(kvp.Key, kvp.Value);
        }
        
        return rt;
    }

    /// <summary>
    /// Inputs exposes the inbound queue so hosts can push messages programmatically.
    /// </summary>
    public ChannelWriter<InputEvent> Inputs() => _inputs.Writer;
    
    /// <summary>
    /// Outputs expose the outbound queue which delivers RuntimeEvents in order.
    /// </summary>
    public ChannelReader<RuntimeEvent> Outputs() => _outputs.Reader;
    
    /// <summary>
    /// SubmitPrompt is a convenience wrapper that enqueues a prompt input.
    /// </summary>
    public async Task SubmitPrompt(string prompt)
    {
        if (IsWorking())
        {
            Emit(new RuntimeEvent
            {
                Type = EventType.Status,
                Message = "Agent is currently executing a plan. Please wait before submitting another prompt.",
                Level = StatusLevel.Warn
            });
            return;
        }
        await Enqueue(new InputEvent { Type = InputEventType.Prompt, Prompt = prompt });
    }
    
    /// <summary>
    /// Cancel enqueues a cancel request, mirroring the TypeScript runtime API.
    /// </summary>
    public async Task Cancel(string reason)
    {
        await Enqueue(new InputEvent { Type = InputEventType.Cancel, Reason = reason });
    }
    
    /// <summary>
    /// Shutdown requests a graceful shutdown of the runtime loop.
    /// </summary>
    public async Task Shutdown(string reason)
    {
        await Enqueue(new InputEvent { Type = InputEventType.Shutdown, Reason = reason });
    }
    
    private async Task QueueHandsFreePrompt()
    {
        if (!_options.HandsFree)
        {
            return;
        }
        
        var topic = _options.HandsFreeTopic?.Trim() ?? "";
        if (string.IsNullOrEmpty(topic))
        {
            return;
        }
        
        await Enqueue(new InputEvent { Type = InputEventType.Prompt, Prompt = topic });
    }
    
    private async Task Enqueue(InputEvent evt)
    {
        if (_closedCts.IsCancellationRequested)
        {
            return;
        }
        
        try
        {
            await _inputs.Writer.WriteAsync(evt, _closedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Runtime is closing
        }
    }
    
    private void Emit(RuntimeEvent evt)
    {
        if (evt.Pass == 0)
        {
            evt.Pass = CurrentPassCount();
        }
        if (string.IsNullOrEmpty(evt.Agent))
        {
            evt.Agent = _agentName;
        }
        
        if (_closedCts.IsCancellationRequested)
        {
            return;
        }
        
        if (_options.EmitTimeout <= TimeSpan.Zero)
        {
            // No timeout: block until sent or runtime is closed
            try
            {
                _outputs.Writer.TryWrite(evt);
            }
            catch
            {
                // Channel may be closed
            }
            return;
        }
        
        // With timeout: attempt to send with a deadline
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_closedCts.Token);
        cts.CancelAfter(_options.EmitTimeout);
        
        try
        {
            if (!_outputs.Writer.TryWrite(evt))
            {
                // If immediate write fails, wait with timeout
                Task.Run(async () =>
                {
                    await _outputs.Writer.WriteAsync(evt, cts.Token);
                }, cts.Token).Wait(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closedCts.IsCancellationRequested)
            {
                // Timeout: channel is full or consumer is blocked
                _options.Logger!.Warn("Event dropped: output channel full or consumer blocked",
                    new LogField("event_type", evt.Type.ToString()),
                    new LogField("timeout_ms", _options.EmitTimeout.TotalMilliseconds),
                    new LogField("output_buffer_size", _options.OutputBuffer)
                );
                _options.Metrics!.RecordDroppedEvent(evt.Type.ToString());
            }
        }
    }
    
    private async Task Close()
    {
        await _closeOnce.WaitAsync();
        try
        {
            if (!_closedCts.IsCancellationRequested)
            {
                _closedCts.Cancel();
                _inputs.Writer.Complete();
                _outputs.Writer.Complete();
                
                // Close log file if one was opened
                if (_logFileCloser != null)
                {
                    try
                    {
                        _logFileCloser.Dispose();
                    }
                    catch (Exception ex)
                    {
                        // Log to stderr since logger might be closed
                        await _options.OutputWriter!.WriteLineAsync($"warning: failed to close log file: {ex.Message}");
                    }
                    _logFileCloser = null;
                }
            }
        }
        finally
        {
            _closeOnce.Release();
        }
    }
    
    private int CurrentPassCount()
    {
        lock (_passLock)
        {
            return _passCount;
        }
    }
    
    private void ResetPassCount()
    {
        lock (_passLock)
        {
            _passCount = 0;
        }
    }
    
    // incrementPassCount increments the session pass counter and returns the latest total.
    private int IncrementPassCount()
    {
        lock (_passLock)
        {
            _passCount++;
            return _passCount;
        }
    }
    
    /// <summary>
    /// Run starts the runtime loop and optionally bridges stdin/stdout to the
    /// respective channels, so the binary is immediately useful in a terminal.
    /// </summary>
    public async Task<Exception?> Run(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ctx = cts.Token;
        
        var tasks = new List<Task>();
        
        if (!_options.DisableOutputForwarding)
        {
            tasks.Add(Task.Run(() => ForwardOutputs(ctx), ctx));
        }
        
        if (_options.HandsFree)
        {
            await QueueHandsFreePrompt();
        }
        
        if (!_options.DisableInputReader && !_options.HandsFree)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ConsumeInput(ctx);
                }
                catch (Exception ex)
                {
                    Emit(new RuntimeEvent
                    {
                        Type = EventType.Error,
                        Message = ex.Message,
                        Level = StatusLevel.Error
                    });
                }
            }, ctx));
        }
        
        var loopErr = await Loop(ctx);
        cts.Cancel();
        
        await Task.WhenAll(tasks.Where(t => !t.IsCompleted));
        
        return loopErr;
    }
    
    private async Task<Exception?> Loop(CancellationToken cancellationToken)
    {
        var traceID = GenerateTraceID();
        _options.Logger!.Info("Agent runtime started",
            new LogField("trace_id", traceID),
            new LogField("agent_name", _agentName),
            new LogField("model", _options.Model)
        );
        Emit(new RuntimeEvent
        {
            Type = EventType.Status,
            Message = "Agent runtime started",
            Level = StatusLevel.Info
        });
        if (!_options.HandsFree)
        {
            EmitRequestInput("Enter a prompt to begin.");
        }
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var evt = await _inputs.Reader.ReadAsync(cancellationToken);
                var err = await HandleInput(cancellationToken, evt);
                if (err != null)
                {
                    _options.Logger!.Error("Error handling input", err);
                    Emit(new RuntimeEvent
                    {
                        Type = EventType.Error,
                        Message = err.Message,
                        Level = StatusLevel.Error
                    });
                    await Close();
                    return err;
                }
            }
            catch (ChannelClosedException)
            {
                await Close();
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _options.Logger!.Warn("Context cancelled, shutting down runtime");
                Emit(new RuntimeEvent
                {
                    Type = EventType.Status,
                    Message = "Context cancelled. Shutting down runtime.",
                    Level = StatusLevel.Warn
                });
                await Close();
                return new OperationCanceledException("Context cancelled");
            }
        }
        
        await Close();
        return null;
    }
    
    private async Task<Exception?> HandleInput(CancellationToken cancellationToken, InputEvent evt)
    {
        switch (evt.Type)
        {
            case InputEventType.Prompt:
                return await HandlePrompt(cancellationToken, evt);
            case InputEventType.Cancel:
                Emit(new RuntimeEvent
                {
                    Type = EventType.Status,
                    Message = $"Cancel requested: {evt.Reason.Trim()}",
                    Level = StatusLevel.Warn
                });
                EmitRequestInput("Ready for the next instruction.");
                return null;
            case InputEventType.Shutdown:
                Emit(new RuntimeEvent
                {
                    Type = EventType.Status,
                    Message = "Shutdown requested. Goodbye!",
                    Level = StatusLevel.Info
                });
                await Close();
                return new Exception("runtime shutdown requested");
            default:
                return new Exception($"unknown input type: {evt.Type}");
        }
    }
    
    private async Task<Exception?> HandlePrompt(CancellationToken cancellationToken, InputEvent evt)
    {
        var prompt = evt.Prompt.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            _options.Logger!.Warn("Ignoring empty prompt");
            Emit(new RuntimeEvent
            {
                Type = EventType.Status,
                Message = "Ignoring empty prompt.",
                Level = StatusLevel.Warn
            });
            EmitRequestInput("Awaiting a non-empty prompt.");
            return null;
        }
        
        if (!await BeginWork())
        {
            _options.Logger!.Warn("Agent is already processing another prompt");
            Emit(new RuntimeEvent
            {
                Type = EventType.Status,
                Message = "Agent is already processing another prompt.",
                Level = StatusLevel.Warn
            });
            return null;
        }
        
        try
        {
            ResetPassCount();
            
            _options.Logger!.Info("Processing user prompt",
                new LogField("prompt_length", prompt.Length)
            );
            
            Emit(new RuntimeEvent
            {
                Type = EventType.Status,
                Message = $"Processing prompt with model {_options.Model}…",
                Level = StatusLevel.Info
            });
            
            var userMessage = new ChatMessage
            {
                Role = MessageRole.User,
                Content = prompt,
                Timestamp = DateTime.Now
            };
            AppendHistory(userMessage);
            
            await PlanExecutionLoop(cancellationToken);
        }
        finally
        {
            await EndWork();
        }
        
        return null;
    }
    
    // planExecutionLoop is implemented in PlanExecution.cs
    
    // requestPlan centralizes the logic for requesting a new plan from the assistant.
    // It snapshots the history to guarantee a consistent view, forwards the request
    // to the OpenAI client, and emits a status update so hosts can surface that a
    // response was received.
    private async Task<(PlanResponse? plan, ToolCall toolCall, Exception? error)> RequestPlan(CancellationToken cancellationToken)
    {
        var retryCount = 0;
        while (true)
        {
            var history = PlanningHistorySnapshot();
            
            WriteHistoryLog(history);
            
            ToolCall toolCall;
            Exception? err;
            
            if (_options.UseStreaming)
            {
                // Stream assistant response using the modern Responses API only.
                // Emit deltas as they arrive and accumulate them to emit a final
                // consolidated message when done.
                var finalBuilder = new StringBuilder();
                void StreamFn(string s)
                {
                    // Do not trim whitespace: models can stream newlines or spaces
                    // as separate deltas for formatting. Only skip truly empty.
                    if (string.IsNullOrEmpty(s))
                    {
                        return;
                    }
                    finalBuilder.Append(s);
                    Emit(new RuntimeEvent { Type = EventType.AssistantDelta, Message = s });
                }
                
                try
                {
                    toolCall = await _client.RequestPlanStreamingResponsesAsync(cancellationToken, history, StreamFn);
                    err = null;
                    // After streaming completes (no error), emit a final assistant message
                    // with the consolidated content so hosts that don't handle deltas can
                    // still present the assistant's reply.
                    var consolidated = finalBuilder.ToString().Trim();
                    if (!string.IsNullOrEmpty(consolidated))
                    {
                        Emit(new RuntimeEvent { Type = EventType.AssistantMessage, Message = consolidated });
                    }
                }
                catch (Exception ex)
                {
                    toolCall = default;
                    err = ex;
                }
            }
            else
            {
                // Non-streaming path preserves historical behavior expected by tests.
                try
                {
                    toolCall = await _client.RequestPlanAsync(cancellationToken, history);
                    err = null;
                }
                catch (Exception ex)
                {
                    toolCall = default;
                    err = ex;
                }
            }
            
            if (err != null)
            {
                _options.Logger!.Error("Failed to request plan from OpenAI", err);
                return (null, default, new Exception($"requestPlan: API request failed: {err.Message}", err));
            }
            
            var (plan, retry, validationErr) = await ValidatePlanToolCall(toolCall, cancellationToken);
            if (validationErr != null)
            {
                _options.Logger!.Error("Plan validation failed", validationErr,
                    new LogField("tool_call_id", toolCall.ID)
                );
                return (null, default, new Exception($"requestPlan: validation failed: {validationErr.Message}", validationErr));
            }
            if (retry)
            {
                retryCount++;
                var delay = ComputeValidationBackoff(retryCount);
                await Task.Delay(delay, cancellationToken);
                continue;
            }
            
            Emit(new RuntimeEvent
            {
                Type = EventType.Status,
                Message = "Assistant response received.",
                Level = StatusLevel.Info
            });
            
            return (plan, toolCall, null);
        }
    }
    
    private async Task<bool> BeginWork()
    {
        await _workMu.WaitAsync();
        try
        {
            if (_working)
            {
                return false;
            }
            _working = true;
            return true;
        }
        finally
        {
            _workMu.Release();
        }
    }
    
    private async Task EndWork()
    {
        await _workMu.WaitAsync();
        try
        {
            _working = false;
        }
        finally
        {
            _workMu.Release();
        }
    }
    
    private bool IsWorking()
    {
        _workMu.Wait();
        try
        {
            return _working;
        }
        finally
        {
            _workMu.Release();
        }
    }
    
    private async Task ConsumeInput(CancellationToken cancellationToken)
    {
        using var reader = _options.InputReader!;
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync();
            }
            catch
            {
                break;
            }
            
            if (line == null)
            {
                await Shutdown("stdin closed");
                return;
            }
            
            line = line.Trim();
            if (IsExitCommand(line))
            {
                await Shutdown("exit command received");
                return;
            }
            
            if (string.Equals(line, "cancel", StringComparison.OrdinalIgnoreCase))
            {
                await Cancel("user requested cancel");
                continue;
            }
            
            await SubmitPrompt(line);
        }
    }
    
    private async Task ForwardOutputs(CancellationToken cancellationToken)
    {
        await foreach (var evt in _outputs.Reader.ReadAllAsync(cancellationToken))
        {
            await _options.OutputWriter!.WriteLineAsync($"[{evt.Type}] {evt.Message}");
        }
    }
    
    private void EmitRequestInput(string message)
    {
        if (_options.HandsFree)
        {
            // In hands-free mode, optionally auto-respond with a configured
            // message to keep execution going without human intervention.
            var reply = _options.HandsFreeAutoReply?.Trim() ?? "";
            if (!string.IsNullOrEmpty(reply))
            {
                // Enqueue a synthetic user prompt to continue the session.
                _ = Enqueue(new InputEvent { Type = InputEventType.Prompt, Prompt = reply });
            }
            return;
        }
        Emit(new RuntimeEvent
        {
            Type = EventType.RequestInput,
            Message = message,
            Level = StatusLevel.Info
        });
    }
    
    private bool IsExitCommand(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }
        foreach (var candidate in _options.ExitCommands)
        {
            if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
    
    private static string GenerateTraceID()
    {
        return DateTime.Now.Ticks.ToString();
    }
    
    private static void RegisterBuiltinInternalCommands(Runtime rt, CommandExecutor executor)
    {
        executor.RegisterInternalCommand(
            InternalCommandApplyPatch.ApplyPatchCommandName,
            InternalCommandApplyPatch.NewApplyPatchCommand()
        );
        executor.RegisterInternalCommand(
            InternalCommandRunResearch.RunResearchCommandName,
            InternalCommandRunResearch.NewRunResearchCommand(rt)
        );
    }
}
