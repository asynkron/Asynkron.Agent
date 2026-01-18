using System.IO;
using System.Text.Json;

namespace Asynkron.Agent.Core.Runtime;

public partial class Runtime
{
    private void AppendHistory(ChatMessage message)
    {
        var pass = CurrentPassCount();
        message.Pass = pass;
        
        _historyMu.EnterWriteLock();
        try
        {
            _history.Add(message);
            ApplyHistoryAmnesiaLocked(pass);
        }
        finally
        {
            _historyMu.ExitWriteLock();
        }
    }
    
    private List<ChatMessage> HistorySnapshot()
    {
        _historyMu.EnterReadLock();
        try
        {
            return new List<ChatMessage>(_history);
        }
        finally
        {
            _historyMu.ExitReadLock();
        }
    }
    
    // planningHistorySnapshot prepares the history for a plan request. It compacts
    // the in-memory slice when the estimated token usage exceeds the configured
    // budget and returns a copy so callers can safely hand it to external clients.
    private List<ChatMessage> PlanningHistorySnapshot()
    {
        _historyMu.EnterWriteLock();
        try
        {
            var limit = _contextBudget.TriggerTokens();
            if (limit > 0)
            {
                var (total, per) = EstimateHistoryTokenUsage(_history);
                if (total > limit)
                {
                    var beforeLen = _history.Count;
                    // Add safeguard: limit iterations to prevent infinite loops
                    // If summarization doesn't reduce tokens enough, we'll stop after max iterations
                    const int maxCompactionIterations = 10;
                    var iterations = 0;
                    while (total > limit && iterations < maxCompactionIterations)
                    {
                        bool changed;
                        (total, per, changed) = CompactHistory(_history, per, total, limit);
                        iterations++;
                        if (!changed)
                        {
                            // No progress made - all eligible messages already summarized
                            // or we can't make progress. Break to avoid infinite loop.
                            break;
                        }
                    }
                    var afterLen = _history.Count;
                    var removed = beforeLen - afterLen;
                    // Note: removed might be 0 if we just summarized without removing entries
                    _options.Metrics!.RecordContextCompaction(removed, afterLen);
                    
                    if (iterations >= maxCompactionIterations && total > limit)
                    {
                        _options.Logger!.Warn("History compaction reached max iterations without meeting budget",
                            new LogField("total_tokens", total),
                            new LogField("limit", limit),
                            new LogField("iterations", iterations)
                        );
                    }
                }
            }
            
            return new List<ChatMessage>(_history);
        }
        finally
        {
            _historyMu.ExitWriteLock();
        }
    }
    
    private void WriteHistoryLog(List<ChatMessage> history)
    {
        // Persist the exact payload forwarded to the model so hosts can inspect it.
        string data;
        try
        {
            data = JsonSerializer.Serialize(history, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
        }
        catch (Exception err)
        {
            Emit(new RuntimeEvent
            {
                Type = EventType.Status,
                Message = $"Failed to encode history log: {err.Message}",
                Level = StatusLevel.Warn
            });
            return;
        }
        
        var historyPath = _options.HistoryLogPath?.Trim() ?? "";
        if (string.IsNullOrEmpty(historyPath))
        {
            return;
        }
        
        try
        {
            File.WriteAllText(historyPath, data);
        }
        catch (Exception err)
        {
            Emit(new RuntimeEvent
            {
                Type = EventType.Status,
                Message = $"Failed to write history log: {err.Message}",
                Level = StatusLevel.Warn
            });
        }
    }
}
