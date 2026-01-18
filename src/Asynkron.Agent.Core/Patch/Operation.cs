namespace Asynkron.Agent.Core.Patch;

/// <summary>
/// Operation describes a high-level instruction contained in a patch payload.
/// </summary>
public sealed class Operation
{
    public OperationType Type { get; set; }
    public string Path { get; set; } = string.Empty;
    public string MovePath { get; set; } = string.Empty;
    public List<Hunk> Hunks { get; set; } = [];
}

/// <summary>
/// OperationType identifies the kind of change described by a patch operation.
/// </summary>
public enum OperationType
{
    Add,
    Update,
    Delete
}

/// <summary>
/// Hunk captures a unified-diff hunk belonging to an Operation.
/// </summary>
public sealed class Hunk
{
    public string Header { get; set; } = string.Empty;
    public List<string> Lines { get; set; } = [];
    public List<string> RawPatchLines { get; set; } = [];
    public List<string> Before { get; set; } = [];
    public List<string> After { get; set; } = [];
    public bool AtEOF { get; set; }
}
