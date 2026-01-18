using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// PlanManager maintains the merged plan shared across passes.
/// </summary>
public sealed class PlanManager
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly List<string> _order = [];
    private readonly Dictionary<string, PlanStep> _steps = new();

    public void Replace(List<PlanStep> steps)
    {
        _lock.EnterWriteLock();
        try
        {
            _steps.Clear();
            _order.Clear();
            
            foreach (var step in steps)
            {
                var copied = step with { Executing = false };
                _steps[step.Id] = copied;
                _order.Add(step.Id);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public List<PlanStep> Snapshot()
    {
        _lock.EnterReadLock();
        try
        {
            var result = new List<PlanStep>(_order.Count);
            foreach (var id in _order)
            {
                if (_steps.TryGetValue(id, out var step))
                {
                    var copied = step with
                    {
                        WaitingForId = step.WaitingForId?.ToList() ?? []
                    };
                    
                    if (step.Observation != null)
                    {
                        var obsCopy = step.Observation with { };
                        if (step.Observation.ObservationForLlm != null)
                        {
                            // Deep copy the payload
                            var payload = step.Observation.ObservationForLlm;
                            var payloadCopy = new PlanObservationPayload
                            {
                                PlanObservation = payload.PlanObservation?.ToList() ?? [],
                                Stdout = payload.Stdout,
                                Stderr = payload.Stderr,
                                Truncated = payload.Truncated,
                                ExitCode = payload.ExitCode,
                                JSONParseError = payload.JSONParseError,
                                SchemaValidationError = payload.SchemaValidationError,
                                ResponseValidationError = payload.ResponseValidationError,
                                CanceledByHuman = payload.CanceledByHuman,
                                OperationCanceled = payload.OperationCanceled,
                                Summary = payload.Summary,
                                Details = payload.Details
                            };
                            obsCopy = obsCopy with { ObservationForLlm = payloadCopy };
                        }
                        copied = copied with { Observation = obsCopy };
                    }
                    
                    result.Add(copied);
                }
            }
            return result;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public (PlanStep? Step, bool Found) Ready()
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var id in _order)
            {
                var step = _steps[id];
                if (StepReadyLocked(step))
                {
                    var updated = step with { Executing = true };
                    _steps[id] = updated;
                    return (updated, true);
                }
            }
            return (null, false);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public int ExecutableCount()
    {
        _lock.EnterReadLock();
        try
        {
            return _order.Count(id => StepReadyLocked(_steps[id]));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private bool StepReadyLocked(PlanStep? step)
    {
        if (step == null || step.Status != PlanStatus.Pending || step.Executing)
        {
            return false;
        }

        if (step.WaitingForId == null)
        {
            return true;
        }

        foreach (var waitId in step.WaitingForId)
        {
            if (!_steps.TryGetValue(waitId, out var dep))
            {
                continue;
            }
            if (dep.Status != PlanStatus.Completed)
            {
                return false;
            }
        }

        return true;
    }

    public void UpdateStatus(string id, PlanStatus status, PlanObservation? observation = null)
    {
        _lock.EnterWriteLock();
        try
        {
            if (!_steps.TryGetValue(id, out var step))
            {
                throw new InvalidOperationException("plan: unknown step id");
            }
            
            _steps[id] = step with 
            { 
                Status = status, 
                Executing = false, 
                Observation = observation ?? step.Observation 
            };
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool HasPending()
    {
        _lock.EnterReadLock();
        try
        {
            return _steps.Values.Any(s => s?.Status == PlanStatus.Pending);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public bool Completed()
    {
        _lock.EnterReadLock();
        try
        {
            return _steps.Count > 0 && _steps.Values.All(s => s?.Status == PlanStatus.Completed);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}
