using System.Collections.Concurrent;

namespace Asynkron.Agent.Core.Runtime;

public partial class Runtime
{
    private static List<PlanStep> FilterCompletedSteps(List<PlanStep> steps)
    {
        if (steps.Count == 0)
        {
            return steps;
        }
        
        var completedIDs = new HashSet<string>();
        var filtered = new List<PlanStep>(steps.Count);
        
        foreach (var step in steps)
        {
            if (step.Status == PlanStatus.Completed)
            {
                completedIDs.Add(step.Id);
                continue;
            }
            filtered.Add(step);
        }
        
        if (completedIDs.Count == 0)
        {
            return filtered;
        }
        
        for (int i = 0; i < filtered.Count; i++)
        {
            var deps = filtered[i].WaitingForId;
            if (deps == null || deps.Count == 0)
            {
                continue;
            }
            
            var trimNeeded = false;
            foreach (var dep in deps)
            {
                if (completedIDs.Contains(dep))
                {
                    trimNeeded = true;
                    break;
                }
            }
            if (!trimNeeded)
            {
                continue;
            }
            
            var pruned = new List<string>();
            foreach (var dep in deps)
            {
                if (!completedIDs.Contains(dep))
                {
                    pruned.Add(dep);
                }
            }
            
            filtered[i] = filtered[i] with 
            { 
                WaitingForId = pruned.Count == 0 ? null : pruned 
            };
        }
        
        return filtered;
    }
    
    private int RecordPlanResponse(PlanResponse plan, ToolCall toolCall)
    {
        var assistantMessage = new ChatMessage
        {
            Role = MessageRole.Assistant,
            Timestamp = DateTime.Now,
            ToolCalls = new List<ToolCall> { toolCall }
        };
        AppendHistory(assistantMessage);
        
        var trimmedPlan = FilterCompletedSteps(plan.Plan);
        _plan.Replace(trimmedPlan);
        
        var planMetadata = new Dictionary<string, object>
        {
            ["plan"] = trimmedPlan,
            ["tool_call_id"] = toolCall.ID,
            ["tool_name"] = toolCall.Name,
            ["require_human_input"] = plan.RequireHumanInput
        };
        
        if (plan.Reasoning != null && plan.Reasoning.Count > 0)
        {
            var reasoning = new List<string>();
            foreach (var entry in plan.Reasoning)
            {
                var trimmed = entry.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }
                reasoning.Add(trimmed);
            }
            if (reasoning.Count > 0)
            {
                planMetadata["reasoning"] = reasoning;
            }
        }
        
        Emit(new RuntimeEvent
        {
            Type = EventType.Status,
            Message = $"Received plan with {trimmedPlan.Count} step(s).",
            Level = StatusLevel.Info,
            Metadata = new Dictionary<string, object>
            {
                ["tool_call_id"] = toolCall.ID,
                ["plan"] = trimmedPlan
            }
        });
        
        Emit(new RuntimeEvent
        {
            Type = EventType.AssistantMessage,
            Message = plan.Message,
            Level = StatusLevel.Info,
            Metadata = planMetadata
        });
        
        return _plan.ExecutableCount();
    }
    
    private async Task ExecutePendingCommands(CancellationToken cancellationToken, ToolCall toolCall)
    {
        await _commandMu.WaitAsync(cancellationToken);
        try
        {
            int executedSteps = 0;
            string lastStepID = "";
            PlanObservationPayload lastObservation = new();
            bool haveObservation = false;
            Exception? finalErr = null;
            
            var orderedResults = new List<StepObservation>();
            
            var results = new BlockingCollection<(PlanStep step, PlanObservationPayload observation, Exception? err)>();
            int executing = 0;
            bool haltScheduling = false;
            
            // scheduleReadySteps launches tasks for every currently-ready step.
            bool ScheduleReadySteps()
            {
                bool started = false;
                if (haltScheduling)
                {
                    return started;
                }
                
                while (!cancellationToken.IsCancellationRequested)
                {
                    var (stepPtr, ok) = _plan.Ready();
                    if (!ok || stepPtr == null)
                    {
                        break;
                    }
                    
                    var step = stepPtr;
                    started = true;
                    
                    var title = step.Title.Trim();
                    if (string.IsNullOrEmpty(title))
                    {
                        title = step.Id;
                    }
                    
                    Emit(new RuntimeEvent
                    {
                        Type = EventType.Status,
                        Message = $"Executing step {step.Id}: {title}",
                        Level = StatusLevel.Info,
                        Metadata = new Dictionary<string, object>
                        {
                            ["step_id"] = step.Id,
                            ["title"] = step.Title,
                            ["command"] = step.Command.Run,
                            ["shell"] = step.Command.Shell,
                            ["cwd"] = step.Command.Cwd
                        }
                    });
                    
                    executing++;
                    
                    // Each worker reports its outcome so the main loop can
                    // record results and schedule additional ready steps.
                    _ = Task.Run(async () =>
                    {
                        var (observation, err) = await _executor!.Execute(cancellationToken, step);
                        results.Add((step, observation, err));
                    }, cancellationToken);
                }
                
                return started;
            }
            
            while (true)
            {
                if (cancellationToken.IsCancellationRequested && finalErr == null)
                {
                    finalErr = new OperationCanceledException();
                }
                
                bool started = ScheduleReadySteps();
                if (executing == 0)
                {
                    if (!started)
                    {
                        if (!_plan.HasPending())
                        {
                            Emit(new RuntimeEvent
                            {
                                Type = EventType.Status,
                                Message = "Plan execution completed.",
                                Level = StatusLevel.Info
                            });
                        }
                        break;
                    }
                }
                
                if (executing == 0)
                {
                    break;
                }
                
                var result = results.Take(cancellationToken);
                executing--;
                
                var step = result.step;
                var observation = result.observation;
                var err = result.err;
                
                executedSteps++;
                lastStepID = step.Id;
                
                var status = PlanStatus.Completed;
                var level = StatusLevel.Info;
                var message = $"Step {step.Id} completed successfully.";
                
                if (err != null)
                {
                    status = PlanStatus.Failed;
                    level = StatusLevel.Error;
                    if (string.IsNullOrEmpty(observation.Details))
                    {
                        observation.Details = err.Message;
                    }
                    message = $"Step {step.Id} failed: {err.Message}";
                    if (finalErr == null)
                    {
                        finalErr = err;
                    }
                    haltScheduling = true;
                }
                
                var stepResult = new StepObservation
                {
                    ID = step.Id,
                    Status = status,
                    Stdout = observation.Stdout,
                    Stderr = observation.Stderr,
                    ExitCode = observation.ExitCode,
                    Details = observation.Details,
                    Truncated = observation.Truncated
                };
                
                // Record metrics for plan step status
                _options.Metrics!.RecordPlanStep(step.Id, status);
                
                var planObservation = new PlanObservation
                {
                    ObservationForLlm = new PlanObservationPayload
                    {
                        PlanObservation = new List<StepObservation> { stepResult }
                    }
                };
                
                try
                {
                    _plan.UpdateStatus(step.Id, status, planObservation);
                }
                catch (Exception updateErr)
                {
                    var wrappedErr = new Exception($"execution: failed to update plan status for step \"{step.Id}\": {updateErr.Message}", updateErr);
                    _options.Logger!.Error("Failed to update plan status", wrappedErr,
                        new LogField("step_id", step.Id),
                        new LogField("status", status.ToString())
                    );
                    Emit(new RuntimeEvent
                    {
                        Type = EventType.Error,
                        Message = $"Failed to update plan status for step {step.Id}: {updateErr.Message}",
                        Level = StatusLevel.Error,
                        Metadata = new Dictionary<string, object>
                        {
                            ["step_id"] = step.Id,
                            ["status"] = status.ToString(),
                            ["error"] = updateErr.Message
                        }
                    });
                    if (finalErr == null)
                    {
                        finalErr = wrappedErr;
                    }
                    haltScheduling = true;
                }
                
                lastObservation = observation;
                haveObservation = true;
                orderedResults.Add(stepResult);
                
                var metadata = new Dictionary<string, object>
                {
                    ["step_id"] = step.Id,
                    ["title"] = step.Title,
                    ["status"] = status.ToString(),
                    ["stdout"] = observation.Stdout,
                    ["stderr"] = observation.Stderr,
                    ["truncated"] = observation.Truncated
                };
                if (observation.ExitCode.HasValue)
                {
                    metadata["exit_code"] = observation.ExitCode.Value;
                }
                if (!string.IsNullOrEmpty(observation.Details))
                {
                    metadata["details"] = observation.Details;
                }
                
                Emit(new RuntimeEvent
                {
                    Type = EventType.Status,
                    Message = message,
                    Level = level,
                    Metadata = metadata
                });
            }
            
            var payload = new PlanObservationPayload
            {
                PlanObservation = orderedResults
            };
            
            if (haveObservation)
            {
                payload.Stdout = lastObservation.Stdout;
                payload.Stderr = lastObservation.Stderr;
                payload.Truncated = lastObservation.Truncated;
                payload.ExitCode = lastObservation.ExitCode;
                payload.Details = lastObservation.Details;
            }
            
            if (string.IsNullOrEmpty(payload.Summary))
            {
                if (executedSteps == 0 && finalErr != null)
                {
                    payload.Summary = "Failed before executing plan steps.";
                }
                else if (executedSteps == 0)
                {
                    payload.Summary = "No plan steps were executed.";
                }
                else if (finalErr != null)
                {
                    payload.Summary = $"Execution halted during step {lastStepID}.";
                }
                else
                {
                    payload.Summary = $"Executed {executedSteps} plan step(s).";
                }
            }
            
            AppendToolObservation(toolCall, payload);
        }
        finally
        {
            _commandMu.Release();
        }
    }
    
    private void AppendToolObservation(ToolCall toolCall, PlanObservationPayload payload)
    {
        if (string.IsNullOrEmpty(toolCall.ID))
        {
            return;
        }
        
        CommandExecutor.EnforceObservationLimit(ref payload);
        
        var (toolMessage, err) = CommandExecutor.BuildToolMessage(payload);
        if (err != null)
        {
            Emit(new RuntimeEvent
            {
                Type = EventType.Error,
                Message = $"Failed to encode tool observation: {err.Message}",
                Level = StatusLevel.Error
            });
            return;
        }
        
        AppendHistory(new ChatMessage
        {
            Role = MessageRole.Tool,
            Content = toolMessage,
            ToolCallID = toolCall.ID,
            Name = toolCall.Name,
            Timestamp = DateTime.Now
        });
    }
}
