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
    public static string FormatError(PatchException? err)
    {
        if (err == null)
        {
            return "Unknown error occurred.";
        }
        var message = err.Message;
        if (string.IsNullOrEmpty(message))
        {
            message = "Unknown error occurred.";
        }
        var code = err.Code;
        if (code == "HUNK_NOT_FOUND" || message.ToLowerInvariant().Contains("hunk not found"))
        {
            var relativePath = err.RelativePath;
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
            var summary = PatchApplier.DescribeHunkStatuses(err.HunkStatuses);
            if (!string.IsNullOrEmpty(summary))
            {
                parts.Add("");
                parts.Add(summary);
            }
            if (err.FailedHunk != null && err.FailedHunk.RawPatchLines.Count > 0)
            {
                parts.Add("");
                parts.Add("Offending hunk:");
                parts.Add(string.Join("\n", err.FailedHunk.RawPatchLines));
            }
            if (!string.IsNullOrEmpty(err.OriginalContent))
            {
                parts.Add("");
                parts.Add($"Full content of file: {displayPath}::::");
                parts.Add(err.OriginalContent);
            }
            return string.Join("\n", parts);
        }
        return message;
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
    public List<string> RawPatchLines { get; set; } = new();
}
