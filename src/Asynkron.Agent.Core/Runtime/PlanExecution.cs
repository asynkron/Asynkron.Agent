namespace Asynkron.Agent.Core.Runtime;

public sealed partial class Runtime
{
    // planExecutionLoop runs the main execution loop, requesting plans and executing steps
    // until completion, error, or interruption.
    private async Task PlanExecutionLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pass = IncrementPassCount();
            _options.Metrics!.RecordPass(pass);
            _options.Logger!.Info("Starting plan execution pass",
                new LogField("pass", pass)
            );
            
            if (CheckPassLimit(pass))
            {
                return;
            }
            
            Emit(new RuntimeEvent
            {
                Type = EventType.Status,
                Message = $"Starting plan execution pass #{pass}.",
                Level = StatusLevel.Info
            });
            
            var (plan, toolCall, err) = await RequestPlan(cancellationToken);
            if (err != null)
            {
                HandlePlanRequestError(err, pass);
                return;
            }
            
            if (plan == null)
            {
                HandleNilPlanResponse(pass);
                return;
            }
            
            var execCount = RecordPlanResponse(plan, toolCall);
            
            if (HandlePlanState(plan, toolCall, execCount, pass))
            {
                return;
            }
            
            await ExecutePendingCommands(cancellationToken, toolCall);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
    
    // checkPassLimit validates if the maximum pass limit has been reached.
    // Returns true if execution should stop.
    private bool CheckPassLimit(int pass)
    {
        if (_options.MaxPasses > 0 && pass > _options.MaxPasses)
        {
            var message = $"Maximum pass limit ({_options.MaxPasses}) reached. Stopping execution.";
            _options.Logger!.Warn("Maximum pass limit reached",
                new LogField("max_passes", _options.MaxPasses),
                new LogField("pass", pass)
            );
            Emit(new RuntimeEvent
            {
                Type = EventType.Error,
                Message = message,
                Level = StatusLevel.Error,
                Metadata = new Dictionary<string, object>
                {
                    ["max_passes"] = _options.MaxPasses,
                    ["pass"] = pass
                }
            });
            EmitRequestInput("Pass limit reached. Provide additional guidance to continue.");
            if (_options.HandsFree)
            {
                _ = Close();
            }
            return true;
        }
        return false;
    }
    
    // handlePlanRequestError handles errors during plan request.
    private void HandlePlanRequestError(Exception err, int pass)
    {
        _options.Logger!.Error("Failed to request plan from OpenAI", err,
            new LogField("pass", pass),
            new LogField("model", _options.Model)
        );
        Emit(new RuntimeEvent
        {
            Type = EventType.Error,
            Message = $"Failed to contact OpenAI (pass {pass}): {err.Message}",
            Level = StatusLevel.Error,
            Metadata = new Dictionary<string, object>
            {
                ["pass"] = pass,
                ["error"] = err.Message
            }
        });
        EmitRequestInput("You can provide another prompt.");
    }
    
    // handleNilPlanResponse handles the case when a nil plan is received.
    private void HandleNilPlanResponse(int pass)
    {
        _options.Logger!.Error("Received nil plan response", null,
            new LogField("pass", pass)
        );
        Emit(new RuntimeEvent
        {
            Type = EventType.Error,
            Message = "Received nil plan response.",
            Level = StatusLevel.Error
        });
        EmitRequestInput("Unable to continue plan execution. Provide the next instruction.");
    }
    
    // handlePlanState processes the plan state and determines if execution should continue.
    // Returns true if execution should stop.
    private bool HandlePlanState(PlanResponse plan, ToolCall toolCall, int execCount, int pass)
    {
        if (plan.RequireHumanInput)
        {
            return HandleHumanInputRequest(toolCall);
        }
        
        if (execCount == 0)
        {
            return HandleEmptyPlan(plan, pass);
        }
        
        return false;
    }
    
    // handleHumanInputRequest handles when the assistant requests human input.
    // Returns true to stop execution and wait for user input.
    private bool HandleHumanInputRequest(ToolCall toolCall)
    {
        AppendToolObservation(toolCall, new PlanObservationPayload
        {
            Summary = "Assistant requested additional input before continuing the plan."
        });
        EmitRequestInput("Assistant requested additional input before continuing.");
        return true;
    }
    
    // handleEmptyPlan handles when the plan has no executable steps.
    // Returns true if execution should stop.
    private bool HandleEmptyPlan(PlanResponse plan, int pass)
    {
        AppendToolObservation(new ToolCall(), new PlanObservationPayload
        {
            Summary = "Assistant returned a plan without executable steps."
        });
        Emit(new RuntimeEvent
        {
            Type = EventType.Status,
            Message = "Plan has no executable steps.",
            Level = StatusLevel.Info
        });
        
        if (_options.HandsFree)
        {
            var summary = $"Hands-free session complete after {pass} pass(es); assistant reported no further work.";
            var trimmed = plan.Message.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                summary = $"{summary} Summary: {trimmed}";
            }
            Emit(new RuntimeEvent
            {
                Type = EventType.Status,
                Message = summary,
                Level = StatusLevel.Info
            });
            _ = Close();
            return true;
        }
        
        EmitRequestInput("Plan has no executable steps. Provide the next instruction.");
        return true;
    }
}
