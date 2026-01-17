namespace Asynkron.Agent.Core.Patch;

/// <summary>
/// PatchOptions configures patch application behavior.
/// </summary>
public class PatchOptions
{
    /// <summary>
    /// IgnoreWhitespace determines whether whitespace differences should be ignored when matching hunks.
    /// </summary>
    public bool IgnoreWhitespace { get; set; }
}

/// <summary>
/// FilesystemOptions augments PatchOptions with a working directory used to resolve
/// relative paths when touching the local filesystem.
/// </summary>
public class FilesystemOptions
{
    /// <summary>
    /// Options contains the base patch application options.
    /// </summary>
    public PatchOptions Options { get; set; } = new();
    
    /// <summary>
    /// WorkingDir is the base directory for resolving relative file paths.
    /// </summary>
    public string WorkingDir { get; set; } = string.Empty;
}
