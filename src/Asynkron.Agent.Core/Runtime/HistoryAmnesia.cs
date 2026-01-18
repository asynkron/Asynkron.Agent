using System.Text.Json;

namespace Asynkron.Agent.Core.Runtime;

public partial class Runtime
{
    private const int AmnesiaAssistantContentLimit = 512;
    private const int AmnesiaToolContentLimit = 512;
    
    // applyHistoryAmnesiaLocked trims bulky history entries once they age beyond the
    // configured pass threshold. Callers must hold historyMu.
    private void ApplyHistoryAmnesiaLocked(int currentPass)
    {
        var threshold = _options.AmnesiaAfterPasses;
        if (threshold <= 0)
        {
            return;
        }
        
        for (int i = 0; i < _history.Count; i++)
        {
            var entry = _history[i];
            if (entry.Role != MessageRole.Assistant && entry.Role != MessageRole.Tool)
            {
                continue;
            }
            if (currentPass - entry.Pass < threshold)
            {
                continue;
            }
            
            switch (entry.Role)
            {
                case MessageRole.Assistant:
                    _history[i] = ScrubAssistantHistoryEntry(entry);
                    break;
                case MessageRole.Tool:
                    _history[i] = ScrubToolHistoryEntry(entry);
                    break;
            }
        }
    }
    
    private static ChatMessage ScrubAssistantHistoryEntry(ChatMessage entry)
    {
        var modified = entry;
        
        if (!string.IsNullOrEmpty(entry.Content))
        {
            modified = modified with 
            { 
                Content = TruncateForPrompt(entry.Content, AmnesiaAssistantContentLimit) 
            };
        }
        
        if (entry.ToolCalls.Count == 0)
        {
            return modified;
        }
        
        var toolCalls = new List<ToolCall>(entry.ToolCalls.Count);
        foreach (var call in entry.ToolCalls)
        {
            if (string.IsNullOrWhiteSpace(call.Arguments))
            {
                toolCalls.Add(call);
                continue;
            }
            toolCalls.Add(new ToolCall
            {
                ID = call.ID,
                Name = call.Name,
                Arguments = TruncateForPrompt(call.Arguments, AmnesiaAssistantContentLimit)
            });
        }
        
        return modified with { ToolCalls = toolCalls };
    }
    
    private static ChatMessage ScrubToolHistoryEntry(ChatMessage entry)
    {
        var raw = entry.Content.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return entry;
        }
        
        PlanObservationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PlanObservationPayload>(raw);
        }
        catch
        {
            return entry with 
            { 
                Content = TruncateForPrompt(raw, AmnesiaToolContentLimit) 
            };
        }
        
        if (payload == null)
        {
            return entry with 
            { 
                Content = TruncateForPrompt(raw, AmnesiaToolContentLimit) 
            };
        }
        
        payload.Stdout = "";
        payload.Stderr = "";
        
        var observations = payload.PlanObservation ?? new List<StepObservation>();
        var scrubbedObservations = new List<StepObservation>(observations.Count);
        foreach (var obs in observations)
        {
            var scrubbed = obs with 
            { 
                Stdout = "", 
                Stderr = "" 
            };
            if (!string.IsNullOrEmpty(obs.Details))
            {
                scrubbed = scrubbed with 
                { 
                    Details = TruncateForPrompt(obs.Details, AmnesiaToolContentLimit) 
                };
            }
            scrubbedObservations.Add(scrubbed);
        }
        payload.PlanObservation = scrubbedObservations;
        
        if (!string.IsNullOrEmpty(payload.Details))
        {
            payload.Details = TruncateForPrompt(payload.Details, AmnesiaToolContentLimit);
        }
        
        var (sanitized, err) = CommandExecutor.BuildToolMessage(payload);
        if (err != null)
        {
            return entry with 
            { 
                Content = TruncateForPrompt(raw, AmnesiaToolContentLimit) 
            };
        }
        
        return entry with { Content = sanitized };
    }
}
