using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// RuntimeLoop contains the main event loop logic for the agent runtime.
/// </summary>
public sealed partial class Runtime
{
    /// <summary>
    /// Run starts the runtime loop and optionally bridges stdin/stdout to the
    /// respective channels, so the binary is immediately useful in a terminal.
    /// </summary>
    public async Task<Exception?> RunAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ctx = cts.Token;

        var tasks = new List<Task>();

        if (!_options.DisableOutputForwarding)
        {
            tasks.Add(Task.Run(() => ForwardOutputsAsync(ctx), ctx));
        }

        if (_options.HandsFree)
        {
            await QueueHandsFreePrompt();
        }

        if (_options is { DisableInputReader: false, HandsFree: false })
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ConsumeInputAsync(ctx);
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

        Exception? loopError = null;
        try
        {
            loopError = await LoopAsync(ctx);
        }
        finally
        {
            await cts.CancelAsync();
            await Task.WhenAll(tasks.Where(t => !t.IsCompleted));
        }

        return loopError;
    }

    private async Task<Exception?> LoopAsync(CancellationToken ctx)
    {
        var traceID = GenerateTraceID();
        _options.Logger.Info("Agent runtime started",
            new LogField("agent_name", _agentName),
            new LogField("model", _options.Model));
        
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

        while (true)
        {
            try
            {
                // ReadAsync will return false when channel is completed
                if (!await _inputs.Reader.WaitToReadAsync(ctx))
                {
                    Close();
                    return null;
                }
                
                var evt = await _inputs.Reader.ReadAsync(ctx);
                
                var err = await HandleInputAsync(ctx, evt);
                if (err != null)
                {
                    _options.Logger.Error("Error handling input", err);
                    Emit(new RuntimeEvent
                    {
                        Type = EventType.Error,
                        Message = err.Message,
                        Level = StatusLevel.Error
                    });
                    Close();
                    return err;
                }
            }
            catch (OperationCanceledException) when (ctx.IsCancellationRequested)
            {
                _options.Logger.Warn("Context cancelled, shutting down runtime");
                Emit(new RuntimeEvent
                {
                    Type = EventType.Status,
                    Message = "Context cancelled. Shutting down runtime.",
                    Level = StatusLevel.Warn
                });
                Close();
                return new OperationCanceledException("Context cancelled");
            }
        }
    }

    private async Task<Exception?> HandleInputAsync(CancellationToken ctx, InputEvent evt)
    {
        switch (evt.Type)
        {
            case InputEventType.Prompt:
                return await HandlePromptAsync(ctx, evt);
            
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
                Close();
                return new Exception("runtime shutdown requested");
            
            default:
                return new Exception($"unknown input type: {evt.Type}");
        }
    }

    private async Task<Exception?> HandlePromptAsync(CancellationToken ctx, InputEvent evt)
    {
        var prompt = evt.Prompt.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            _options.Logger.Warn("Ignoring empty prompt");
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
            _options.Logger.Warn("Agent is already processing another prompt");
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

            _options.Logger.Info("Processing user prompt",
                new LogField("prompt_length", prompt.Length));

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
                Timestamp = DateTime.UtcNow
            };
            AppendHistory(userMessage);

            await PlanExecutionLoop(ctx);

            return null;
        }
        finally
        {
            await EndWork();
        }
    }

    /// <summary>
    /// RequestPlan centralizes the logic for requesting a new plan from the assistant.
    /// It snapshots the history to guarantee a consistent view, forwards the request
    /// to the OpenAI client, and emits a status update so hosts can surface that a
    /// response was received.
    /// </summary>
    private async Task<(PlanResponse? plan, ToolCall? toolCall, Exception? error)> RequestPlanAsync(CancellationToken ctx)
    {
        var retryCount = 0;
        
        while (true)
        {
            var history = PlanningHistorySnapshot();
            WriteHistoryLog(history);

            ToolCall toolCall;
            Exception? err = null;

            if (_options.UseStreaming)
            {
                // Stream assistant response using the modern Responses API only.
                // Emit deltas as they arrive and accumulate them to emit a final
                // consolidated message when done.
                var finalBuilder = new System.Text.StringBuilder();

                void StreamFn(string s)
                {
                    // Do not trim whitespace: models can stream newlines or spaces
                    // as separate deltas for formatting. Only skip truly empty.
                    if (string.IsNullOrEmpty(s)) return;

                    finalBuilder.Append(s);
                    Emit(new RuntimeEvent { Type = EventType.AssistantDelta, Message = s });
                }

                try
                {
                    toolCall = await _client.RequestPlanStreamingResponsesAsync(ctx, history, StreamFn);
                }
                catch (Exception ex)
                {
                    err = ex;
                    toolCall = null!;
                }
                
                // After streaming completes (no error), emit a final assistant message
                // with the consolidated content so hosts that don't handle deltas can
                // still present the assistant's reply.
                if (err == null)
                {
                    var consolidated = finalBuilder.ToString().Trim();
                    if (!string.IsNullOrEmpty(consolidated))
                    {
                        Emit(new RuntimeEvent { Type = EventType.AssistantMessage, Message = consolidated });
                    }
                }
            }
            else
            {
                // Non-streaming path preserves historical behavior expected by tests.
                try
                {
                    toolCall = await _client.RequestPlanAsync(ctx, history);
                }
                catch (Exception ex)
                {
                    err = ex;
                    toolCall = null!;
                }
            }

            if (err != null)
            {
                _options.Logger.Error("Failed to request plan from OpenAI", err);
                return (null, null, new Exception($"requestPlan: API request failed: {err.Message}", err));
            }

            var validationResult = await ValidatePlanToolCall(toolCall, ctx);
            var plan = validationResult.Item1;
            var retry = validationResult.Item2;
            var validationErr = validationResult.Item3;
            if (validationErr != null)
            {
                _options.Logger.Error("Plan validation failed", validationErr,
                    new LogField("tool_call_id", toolCall.ID));
                return (null, null, new Exception($"requestPlan: validation failed: {validationErr.Message}", validationErr));
            }

            if (retry)
            {
                retryCount++;
                var delay = ComputeValidationBackoff(retryCount);
                try
                {
                    await Task.Delay(delay, ctx);
                }
                catch (OperationCanceledException)
                {
                    return (null, null, new OperationCanceledException());
                }
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

    private async Task ConsumeInputAsync(CancellationToken ctx)
    {
        // Use the InputReader directly as a TextReader
        var reader = _options.InputReader;
        
        while (!ctx.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (line == null)
            {
                await Shutdown("stdin closed");
                return;
            }

            var trimmed = line.Trim();
            if (IsExitCommand(trimmed))
            {
                await Shutdown("exit command received");
                return;
            }

            if (string.Equals(trimmed, "cancel", StringComparison.OrdinalIgnoreCase))
            {
                await Cancel("user requested cancel");
                continue;
            }

            await SubmitPrompt(trimmed);
        }
    }

    private async Task ForwardOutputsAsync(CancellationToken ctx)
    {
        await foreach (var evt in _outputs.Reader.ReadAllAsync(ctx))
        {
            await _options.OutputWriter!.WriteAsync($"[{evt.Type}] {evt.Message}\n");
            await _options.OutputWriter.FlushAsync(ctx);
        }
    }
}
