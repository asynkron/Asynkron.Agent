namespace Asynkron.Agent.Core.Patch;

/// <summary>
/// PatchResult describes the outcome for a single file when applying a patch.
/// </summary>
public sealed class PatchResult
{
    public string Status { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
