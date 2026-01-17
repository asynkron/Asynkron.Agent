using System.Text.Json;

namespace Asynkron.Agent.Core.Runtime;

internal static class InternalCommandRunResearch
{
    internal const string RunResearchCommandName = "run_research";

    internal static InternalCommandHandlerAsync NewRunResearchCommand(Runtime rt)
    {
        return async (req, cancellationToken) =>
        {
            var payload = new PlanObservationPayload();

            // 1. Parse the research spec from the raw command
            var jsonInput = req.Raw.Trim();
            if (jsonInput.StartsWith(RunResearchCommandName))
            {
                jsonInput = jsonInput.Substring(RunResearchCommandName.Length).Trim();
            }

            ResearchSpec rs;
            try
            {
                rs = JsonSerializer.Deserialize<ResearchSpec>(jsonInput) ?? new ResearchSpec();
            }
            catch
            {
                return FailRunResearch(ref payload, "internal command: run_research invalid JSON");
            }

            rs.Goal = rs.Goal.Trim();
            if (string.IsNullOrEmpty(rs.Goal))
            {
                return FailRunResearch(ref payload, "internal command: run_research requires non-empty goal");
            }
            if (rs.Turns <= 0)
            {
                rs.Turns = 10; // Default to 10 turns if not specified or invalid
            }

            // 2. Configure new runtime options for the sub-agent
            var subOptions = rt._options with
            {
                HandsFree = true,
                HandsFreeTopic = rs.Goal,
                MaxPasses = rs.Turns,
                HandsFreeAutoReply = $"Please continue to work on the set goal. No human available. Goal: {rs.Goal}",
                DisableInputReader = true,
                DisableOutputForwarding = true
            };

            // 3. Create and run the sub-agent
            Runtime subAgent;
            try
            {
                subAgent = Runtime.NewRuntime(subOptions);
            }
            catch
            {
                return FailRunResearch(ref payload, "failed to create sub-agent");
            }

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var runCtx = runCts.Token;
            
            _ = Task.Run(async () =>
            {
                try
                {
                    await subAgent.Run(runCtx);
                }
                catch
                {
                    // Errors are handled through the output channel
                }
            }, runCtx);

            // 4. Capture the output of the sub-agent
            var lastAssistant = string.Empty;
            var success = false;
            
            try
            {
                await foreach (var evt in subAgent.Outputs().ReadAllAsync(runCtx))
                {
                    switch (evt.Type)
                    {
                        case EventType.AssistantMessage:
                            var m = evt.Message.Trim();
                            if (!string.IsNullOrEmpty(m))
                            {
                                lastAssistant = m;
                            }
                            break;
                        case EventType.Status:
                            if (evt.Message.Contains("Hands-free session complete"))
                            {
                                success = true;
                            }
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when context is cancelled
            }

            // 5. Populate the payload with the result
            if (success)
            {
                payload.Stdout = lastAssistant;
                payload.ExitCode = 0;
            }
            else
            {
                payload.Stderr = lastAssistant;
                payload.ExitCode = 1;
            }

            return payload;
        };
    }

    private static PlanObservationPayload FailRunResearch(ref PlanObservationPayload payload, string message)
    {
        payload.Stderr = message;
        payload.Details = message;
        payload.ExitCode = 1;
        return payload;
    }

    private class ResearchSpec
    {
        [System.Text.Json.Serialization.JsonPropertyName("goal")]
        public string Goal { get; set; } = string.Empty;
        
        [System.Text.Json.Serialization.JsonPropertyName("turns")]
        public int Turns { get; set; }
    }
}
