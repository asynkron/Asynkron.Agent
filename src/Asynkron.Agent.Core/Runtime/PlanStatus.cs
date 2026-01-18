namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// PlanStatus represents execution status for a plan step.
/// </summary>
public enum PlanStatus
{
    Pending,
    Completed,
    Failed,
    Abandoned
}
