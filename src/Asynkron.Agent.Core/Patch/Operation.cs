namespace Asynkron.Agent.Core.Patch;

/// <summary>
/// Operation describes a high-level instruction contained in a patch payload.
/// </summary>
public class Operation
{
    public OperationType Type { get; set; }
    public string Path { get; set; } = string.Empty;
    public string MovePath { get; set; } = string.Empty;
    public List<Hunk> Hunks { get; set; } = new();
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
public class Hunk
{
    public string Header { get; set; } = string.Empty;
    public List<string> Lines { get; set; } = new();
    public List<string> RawPatchLines { get; set; } = new();
    public List<string> Before { get; set; } = new();
    public List<string> After { get; set; } = new();
    public bool AtEOF { get; set; }
}
