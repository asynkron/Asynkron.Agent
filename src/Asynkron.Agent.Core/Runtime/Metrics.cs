using System;
using System.Collections.Generic;
using System.Threading;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// Metrics collects runtime metrics for monitoring and observability.
/// </summary>
public interface IMetrics
{
    void RecordAPICall(TimeSpan duration, bool success);
    void RecordCommandExecution(string stepId, TimeSpan duration, bool success);
    void RecordContextCompaction(int removed, int remaining);
    void RecordPlanStep(string stepId, PlanStatus status);
    void RecordPass(int passNumber);
    void RecordDroppedEvent(string eventType);
    MetricsSnapshot GetSnapshot();
    void Reset();
}

/// <summary>
/// MetricsSnapshot contains a point-in-time view of collected metrics.
/// </summary>
public record MetricsSnapshot
{
    public ApiCallMetrics ApiCalls { get; init; } = new();
    public CommandExecutionMetrics CommandExecutions { get; init; } = new();
    public long ContextCompactions { get; init; }
    public Dictionary<string, long> PlanSteps { get; init; } = new();
    public long TotalPasses { get; init; }
    public long DroppedEvents { get; init; }
    public DateTime LastAPICallTime { get; init; }
    public DateTime LastCommandTime { get; init; }
}

/// <summary>
/// APICallMetrics tracks OpenAI API call statistics.
/// </summary>
public record ApiCallMetrics
{
    public long Total { get; init; }
    public long Success { get; init; }
    public long Failed { get; init; }
    public TimeSpan TotalTime { get; init; }
    public TimeSpan MinTime { get; init; }
    public TimeSpan MaxTime { get; init; }
}

/// <summary>
/// CommandExecutionMetrics tracks command execution statistics.
/// </summary>
public record CommandExecutionMetrics
{
    public long Total { get; init; }
    public long Success { get; init; }
    public long Failed { get; init; }
    public TimeSpan TotalTime { get; init; }
    public TimeSpan MinTime { get; init; }
    public TimeSpan MaxTime { get; init; }
}

/// <summary>
/// NoOpMetrics is a metrics collector that discards all metrics.
/// </summary>
public class NoOpMetrics : IMetrics
{
    public void RecordAPICall(TimeSpan duration, bool success) { }
    public void RecordCommandExecution(string stepId, TimeSpan duration, bool success) { }
    public void RecordContextCompaction(int removed, int remaining) { }
    public void RecordPlanStep(string stepId, PlanStatus status) { }
    public void RecordPass(int passNumber) { }
    public void RecordDroppedEvent(string eventType) { }
    public MetricsSnapshot GetSnapshot() => new();
    public void Reset() { }
}

/// <summary>
/// InMemoryMetrics is a thread-safe in-memory metrics collector.
/// </summary>
public class InMemoryMetrics : IMetrics
{
    private readonly ReaderWriterLockSlim _lock = new();
    private ApiCallMetrics _apiCalls = new();
    private CommandExecutionMetrics _commandExecutions = new();
    private long _contextCompactions;
    private readonly Dictionary<string, long> _planSteps = new();
    private long _totalPasses;
    private long _droppedEvents;
    private DateTime _lastApiCallTime;
    private DateTime _lastCommandTime;

    private long _apiMinTimeNs = TimeSpan.FromHours(1).Ticks * 100;
    private long _apiMaxTimeNs;
    private long _cmdMinTimeNs = TimeSpan.FromHours(1).Ticks * 100;
    private long _cmdMaxTimeNs;

    public void RecordAPICall(TimeSpan duration, bool success)
    {
        _lock.EnterWriteLock();
        try
        {
            var total = _apiCalls.Total + 1;
            var successCount = success ? _apiCalls.Success + 1 : _apiCalls.Success;
            var failed = success ? _apiCalls.Failed : _apiCalls.Failed + 1;
            var totalTime = _apiCalls.TotalTime + duration;
            
            _apiCalls = _apiCalls with
            {
                Total = total,
                Success = successCount,
                Failed = failed,
                TotalTime = totalTime
            };
            _lastApiCallTime = DateTime.UtcNow;

            var durNanos = duration.Ticks * 100;
            UpdateMinMax(ref _apiMinTimeNs, ref _apiMaxTimeNs, durNanos);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void RecordCommandExecution(string stepId, TimeSpan duration, bool success)
    {
        _lock.EnterWriteLock();
        try
        {
            var total = _commandExecutions.Total + 1;
            var successCount = success ? _commandExecutions.Success + 1 : _commandExecutions.Success;
            var failed = success ? _commandExecutions.Failed : _commandExecutions.Failed + 1;
            var totalTime = _commandExecutions.TotalTime + duration;
            
            _commandExecutions = _commandExecutions with
            {
                Total = total,
                Success = successCount,
                Failed = failed,
                TotalTime = totalTime
            };
            _lastCommandTime = DateTime.UtcNow;

            var durNanos = duration.Ticks * 100;
            UpdateMinMax(ref _cmdMinTimeNs, ref _cmdMaxTimeNs, durNanos);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void RecordContextCompaction(int removed, int remaining)
    {
        Interlocked.Increment(ref _contextCompactions);
    }

    public void RecordPlanStep(string stepId, PlanStatus status)
    {
        _lock.EnterWriteLock();
        try
        {
            var statusStr = status.ToString();
            _planSteps.TryGetValue(statusStr, out var count);
            _planSteps[statusStr] = count + 1;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void RecordPass(int passNumber)
    {
        Interlocked.Increment(ref _totalPasses);
    }

    public void RecordDroppedEvent(string eventType)
    {
        Interlocked.Increment(ref _droppedEvents);
    }

    public MetricsSnapshot GetSnapshot()
    {
        _lock.EnterReadLock();
        try
        {
            var snapshot = new MetricsSnapshot
            {
                ApiCalls = _apiCalls with
                {
                    MinTime = TimeSpan.FromTicks(Interlocked.Read(ref _apiMinTimeNs) / 100),
                    MaxTime = TimeSpan.FromTicks(Interlocked.Read(ref _apiMaxTimeNs) / 100)
                },
                CommandExecutions = _commandExecutions with
                {
                    MinTime = TimeSpan.FromTicks(Interlocked.Read(ref _cmdMinTimeNs) / 100),
                    MaxTime = TimeSpan.FromTicks(Interlocked.Read(ref _cmdMaxTimeNs) / 100)
                },
                ContextCompactions = Interlocked.Read(ref _contextCompactions),
                PlanSteps = new Dictionary<string, long>(_planSteps),
                TotalPasses = Interlocked.Read(ref _totalPasses),
                DroppedEvents = Interlocked.Read(ref _droppedEvents),
                LastAPICallTime = _lastApiCallTime,
                LastCommandTime = _lastCommandTime
            };

            if (snapshot.ApiCalls.Total == 0)
            {
                snapshot = snapshot with
                {
                    ApiCalls = snapshot.ApiCalls with { MinTime = TimeSpan.Zero, MaxTime = TimeSpan.Zero }
                };
            }

            if (snapshot.CommandExecutions.Total == 0)
            {
                snapshot = snapshot with
                {
                    CommandExecutions = snapshot.CommandExecutions with { MinTime = TimeSpan.Zero, MaxTime = TimeSpan.Zero }
                };
            }

            return snapshot;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Reset()
    {
        _lock.EnterWriteLock();
        try
        {
            _apiCalls = new ApiCallMetrics();
            _commandExecutions = new CommandExecutionMetrics();
            Interlocked.Exchange(ref _contextCompactions, 0);
            _planSteps.Clear();
            Interlocked.Exchange(ref _totalPasses, 0);
            Interlocked.Exchange(ref _droppedEvents, 0);
            _lastApiCallTime = DateTime.MinValue;
            _lastCommandTime = DateTime.MinValue;
            Interlocked.Exchange(ref _apiMinTimeNs, TimeSpan.FromHours(1).Ticks * 100);
            Interlocked.Exchange(ref _apiMaxTimeNs, 0);
            Interlocked.Exchange(ref _cmdMinTimeNs, TimeSpan.FromHours(1).Ticks * 100);
            Interlocked.Exchange(ref _cmdMaxTimeNs, 0);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private static void UpdateMinMax(ref long minVal, ref long maxVal, long newVal)
    {
        long currentMin;
        do
        {
            currentMin = Interlocked.Read(ref minVal);
            if (newVal >= currentMin) break;
        } while (Interlocked.CompareExchange(ref minVal, newVal, currentMin) != currentMin);

        long currentMax;
        do
        {
            currentMax = Interlocked.Read(ref maxVal);
            if (newVal <= currentMax) break;
        } while (Interlocked.CompareExchange(ref maxVal, newVal, currentMax) != currentMax);
    }
}
