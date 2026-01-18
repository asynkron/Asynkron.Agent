using System.Text;
using System.Text.Json;

namespace Asynkron.Agent.Core.Runtime;

public sealed partial class Runtime
{
    private const string SummaryPrefix = "[summary]";
    private const int SummarySnippetSize = 160;
    
    // estimateHistoryTokenUsage walks the history and returns the total estimated
    // token usage together with the per-message contribution. The heuristic is
    // intentionally simple (roughly four characters per token) which keeps the
    // estimator fast while still providing a useful signal for trimming.
    private static (int total, List<int> per) EstimateHistoryTokenUsage(List<ChatMessage> history)
    {
        var totals = new List<int>(history.Count);
        var sum = 0;
        foreach (var msg in history)
        {
            var tokens = EstimateMessageTokens(msg);
            totals.Add(tokens);
            sum += tokens;
        }
        return (sum, totals);
    }
    
    // estimateMessageTokens approximates the token usage of an individual message
    // using a character based heuristic. We include a small base overhead so that
    // very short messages still contribute to the budget.
    private static int EstimateMessageTokens(ChatMessage message)
    {
        const int baseOverhead = 4;
        var total = baseOverhead;
        
        total += EstimateStringTokens(message.Role.ToString());
        total += EstimateStringTokens(message.Content);
        total += EstimateStringTokens(message.ToolCallID ?? "");
        total += EstimateStringTokens(message.Name ?? "");
        
        foreach (var call in message.ToolCalls)
        {
            total += baseOverhead;
            total += EstimateStringTokens(call.ID);
            total += EstimateStringTokens(call.Name);
            total += EstimateStringTokens(call.Arguments);
        }
        
        return total;
    }
    
    private static int EstimateStringTokens(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }
        var runes = value.Length; // Approximation - not exact rune count
        var tokens = (int)Math.Ceiling(runes / 4.0);
        if (tokens < 1)
        {
            tokens = 1;
        }
        return tokens;
    }
    
    // compactHistory replaces the oldest non-system messages with summaries until
    // the history drops below the provided limit or no further compaction is
    // possible. The slice is modified in place, preserving ordering.
    private static (int total, List<int> per, bool changed) CompactHistory(
        List<ChatMessage> history, 
        List<int> per, 
        int total, 
        int limit)
    {
        if (limit <= 0)
        {
            return (total, per, false);
        }
        var changed = false;
        for (int i = 0; i < history.Count; i++)
        {
            if (total <= limit)
            {
                break;
            }
            var message = history[i];
            if (message.Role == MessageRole.System || message.Summarized)
            {
                continue;
            }
            
            var summary = SynthesizeSummary(message);
            var summaryTokens = EstimateMessageTokens(summary);
            
            if (i < per.Count)
            {
                total -= per[i];
                per[i] = summaryTokens;
            }
            else
            {
                per.Add(summaryTokens);
            }
            total += summaryTokens;
            history[i] = summary;
            changed = true;
        }
        return (total, per, changed);
    }
    
    private static ChatMessage SynthesizeSummary(ChatMessage message)
    {
        var summary = new ChatMessage
        {
            Role = MessageRole.Assistant,
            Timestamp = message.Timestamp,
            Summarized = true
        };
        
        summary.Content = message.Role switch
        {
            MessageRole.Tool => BuildToolSummary(message.Content),
            MessageRole.User => BuildConversationSummary("User", message.Content),
            MessageRole.Assistant => BuildConversationSummary("Assistant", message.Content),
            _ => BuildConversationSummary("Message", message.Content)
        };
        
        if (string.IsNullOrEmpty(summary.Content))
        {
            summary.Content = $"{SummaryPrefix} Conversation context compressed.";
        }
        
        return summary;
    }
    
    private static string BuildConversationSummary(string label, string content)
    {
        var snippet = CompactSnippet(content);
        if (string.IsNullOrEmpty(snippet))
        {
            return "";
        }
        return $"{SummaryPrefix} {label.ToLowerInvariant()} recap: {snippet}";
    }
    
    private static string BuildToolSummary(string content)
    {
        PlanObservationPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PlanObservationPayload>(content);
        }
        catch
        {
            var snippet = CompactSnippet(content);
            if (string.IsNullOrEmpty(snippet))
            {
                return $"{SummaryPrefix} tool observation compacted.";
            }
            return $"{SummaryPrefix} tool observation recap: {snippet}";
        }
        
        if (payload == null)
        {
            return $"{SummaryPrefix} tool observation compacted.";
        }
        
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(payload.Summary))
        {
            parts.Add(payload.Summary);
        }
        if (!string.IsNullOrEmpty(payload.Details))
        {
            parts.Add(payload.Details);
        }
        foreach (var step in payload.PlanObservation ?? [])
        {
            if (string.IsNullOrEmpty(step.ID) && string.IsNullOrEmpty(step.Status.ToString()))
            {
                continue;
            }
            var label = step.ID;
            if (string.IsNullOrEmpty(label))
            {
                label = "step";
            }
            parts.Add($"{label}={step.Status}");
            if (parts.Count >= 6)
            {
                break;
            }
        }
        if (payload.CanceledByHuman)
        {
            parts.Add("canceled by human");
        }
        if (payload.OperationCanceled)
        {
            parts.Add("operation canceled");
        }
        if (payload.Truncated)
        {
            parts.Add("output truncated");
        }
        
        var snippetResult = CompactSnippet(string.Join("; ", parts));
        if (string.IsNullOrEmpty(snippetResult))
        {
            return $"{SummaryPrefix} tool observation compacted.";
        }
        return $"{SummaryPrefix} tool observation: {snippetResult}";
    }
    
    private static string CompactSnippet(string input)
    {
        var trimmed = input.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "";
        }
        // Collapse whitespace so we keep the snippet short and legible.
        trimmed = string.Join(" ", trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (trimmed.Length <= SummarySnippetSize)
        {
            return trimmed;
        }
        return trimmed[..SummarySnippetSize] + "…";
    }
}
