namespace Asynkron.Agent.Core.Patch;

/// <summary>
/// PatchException represents an error that occurred during patch application.
/// </summary>
public class PatchException : Exception
{
    public string Code { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string OriginalContent { get; set; } = string.Empty;
    public List<HunkStatus> HunkStatuses { get; set; } = new();
    public FailedHunk? FailedHunk { get; set; }

    public PatchException(string message) : base(message)
    {
    }

    public PatchException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// FormatError produces a human-readable error message from a PatchException.
    /// </summary>
    public static string FormatError(PatchException err)
    {
        if (err == null)
        {
            return string.Empty;
        }
        return err.Message;
    }
}

/// <summary>
/// HunkStatus tracks how a hunk was applied when processing a patch.
/// </summary>
public class HunkStatus
{
    public int Number { get; set; }
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// FailedHunk stores the raw lines of the hunk that could not be applied.
/// </summary>
public class FailedHunk
{
    public int Number { get; set; }
    public List<string> Lines { get; set; } = new();
}
