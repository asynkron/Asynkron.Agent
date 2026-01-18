namespace Asynkron.Agent.Core.Patch;

/// <summary>
/// PatchException represents an error that occurred during patch application.
/// </summary>
public sealed class PatchException : Exception
{
    public string Code { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string OriginalContent { get; set; } = string.Empty;
    public List<HunkStatus> HunkStatuses { get; set; } = [];
    public FailedHunk? FailedHunk { get; set; }

    public PatchException(string message) : base(message)
    {
    }

    public PatchException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// Returns a detailed error message including hunk status and file content when applicable.
    /// </summary>
    public string ToDetailedString()
    {
        var message = Message;
        if (string.IsNullOrEmpty(message))
        {
            message = "Unknown error occurred.";
        }
        
        if (Code == "HUNK_NOT_FOUND" || message.ToLowerInvariant().Contains("hunk not found"))
        {
            var relativePath = RelativePath;
            if (string.IsNullOrEmpty(relativePath))
            {
                relativePath = "unknown file";
            }
            var displayPath = relativePath;
            if (!displayPath.StartsWith("./"))
            {
                displayPath = "./" + displayPath;
            }
            var parts = new List<string> { message };
            var summary = PatchApplier.DescribeHunkStatuses(HunkStatuses);
            if (!string.IsNullOrEmpty(summary))
            {
                parts.Add("");
                parts.Add(summary);
            }
            if (FailedHunk is { RawPatchLines.Count: > 0 })
            {
                parts.Add("");
                parts.Add("Offending hunk:");
                parts.Add(string.Join("\n", FailedHunk.RawPatchLines));
            }
            if (!string.IsNullOrEmpty(OriginalContent))
            {
                parts.Add("");
                parts.Add($"Full content of file: {displayPath}::::");
                parts.Add(OriginalContent);
            }
            return string.Join("\n", parts);
        }
        return message;
    }

    /// <summary>
    /// FormatError produces a human-readable error message from a PatchException.
    /// </summary>
    [Obsolete("Use ToDetailedString() instance method instead")]
    public static string FormatError(PatchException? err)
    {
        if (err == null)
        {
            return "Unknown error occurred.";
        }
        return err.ToDetailedString();
    }
}

/// <summary>
/// HunkStatus tracks how a hunk was applied when processing a patch.
/// </summary>
public sealed class HunkStatus
{
    public int Number { get; set; }
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// FailedHunk stores the raw lines of the hunk that could not be applied.
/// </summary>
public sealed class FailedHunk
{
    public int Number { get; set; }
    public List<string> RawPatchLines { get; set; } = [];
}
