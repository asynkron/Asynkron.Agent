using System.Text;

namespace Asynkron.Agent.Core.Patch;

/// <summary>
/// PatchApplier applies parsed patch operations to content.
/// </summary>
public static class PatchApplier
{
    internal interface IWorkspace
    {
        FileState Ensure(string path, bool create);
        void Delete(string path);
        List<PatchResult> Commit();
    }

    internal class FileState
    {
        public string Path { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public List<string> Lines { get; set; } = new();
        public List<string>? NormalizedLines { get; set; }
        public string OriginalContent { get; set; } = string.Empty;
        public bool? OriginalEndsWithNewline { get; set; }
        public int OriginalMode { get; set; }
        public bool Touched { get; set; }
        public int Cursor { get; set; }
        public List<HunkStatus> HunkStatuses { get; set; } = new();
        public bool IsNew { get; set; }
        public string MovePath { get; set; } = string.Empty;
        public PatchOptions Options { get; set; } = new();
    }

    internal static List<PatchResult> Apply(CancellationToken cancellationToken, List<Operation> operations, IWorkspace ws)
    {
        if (ws == null)
        {
            throw new ArgumentNullException(nameof(ws));
        }

        foreach (var op in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (op.Type)
            {
                case OperationType.Delete:
                    try
                    {
                        ws.Delete(op.Path);
                    }
                    catch (PatchException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw new PatchException(ex.Message);
                    }
                    break;

                case OperationType.Update:
                case OperationType.Add:
                    FileState state;
                    try
                    {
                        state = ws.Ensure(op.Path, op.Type == OperationType.Add);
                    }
                    catch (PatchException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        throw new PatchException(ex.Message);
                    }

                    state.Cursor = 0;
                    state.HunkStatuses.Clear();

                    for (int index = 0; index < op.Hunks.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var hunk = op.Hunks[index];
                        var number = index + 1;
                        try
                        {
                            ApplyHunk(state, hunk);
                        }
                        catch (PatchException ex)
                        {
                            throw EnhanceHunkError(ex, state, hunk, number);
                        }
                        catch (Exception ex)
                        {
                            throw EnhanceHunkError(new PatchException(ex.Message), state, hunk, number);
                        }
                        state.HunkStatuses.Add(new HunkStatus { Number = number, Status = "applied" });
                        state.Touched = true;
                    }

                    var trimmedMove = op.MovePath?.Trim();
                    if (!string.IsNullOrEmpty(trimmedMove))
                    {
                        state.MovePath = trimmedMove;
                        state.Touched = true;
                    }
                    break;

                default:
                    throw new PatchException($"unsupported patch operation for {op.Path}: {op.Type}");
            }
        }

        try
        {
            return ws.Commit();
        }
        catch (PatchException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PatchException(ex.Message);
        }
    }

    internal static void ApplyHunk(FileState state, Hunk hunk)
    {
        if (state == null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        var before = hunk.Before;
        var after = hunk.After;

        if (before.Count == 0)
        {
            var insertionIndex = state.Lines.Count;
            if (insertionIndex > 0 && state.Lines[insertionIndex - 1] == "")
            {
                insertionIndex--;
            }
            state.Lines = Splice(state.Lines, insertionIndex, 0, after);
            UpdateNormalizedLines(state, insertionIndex, 0, after);
            state.Cursor = insertionIndex + after.Count;
            return;
        }

        var matchIndex = FindSubsequence(state.Lines, before, state.Cursor, hunk.AtEOF);
        if (matchIndex == -1)
        {
            matchIndex = FindSubsequence(state.Lines, before, 0, hunk.AtEOF);
        }

        if (matchIndex == -1 && state.Options.IgnoreWhitespace)
        {
            var normalizedBefore = before.Select(NormalizeLine).ToList();
            var normalizedLines = EnsureNormalizedLines(state);
            matchIndex = FindSubsequence(normalizedLines, normalizedBefore, state.Cursor, hunk.AtEOF);
            if (matchIndex == -1)
            {
                matchIndex = FindSubsequence(normalizedLines, normalizedBefore, 0, hunk.AtEOF);
            }
        }

        if (matchIndex == -1)
        {
            var message = $"Hunk not found in {state.RelativePath}.";
            var original = !string.IsNullOrEmpty(state.OriginalContent)
                ? state.OriginalContent
                : string.Join("\n", state.Lines);
            throw new PatchException(message)
            {
                Code = "HUNK_NOT_FOUND",
                RelativePath = state.RelativePath,
                OriginalContent = original
            };
        }

        state.Lines = Splice(state.Lines, matchIndex, before.Count, after);
        UpdateNormalizedLines(state, matchIndex, before.Count, after);
        state.Cursor = matchIndex + after.Count;
    }

    internal static List<string> Splice(List<string> target, int index, int deleteCount, List<string> replacement)
    {
        if (deleteCount == 0 && replacement.Count == 0)
        {
            return target;
        }
        var result = new List<string>(target.Count - deleteCount + replacement.Count);
        result.AddRange(target.Take(index));
        result.AddRange(replacement);
        result.AddRange(target.Skip(index + deleteCount));
        return result;
    }

    internal static int FindSubsequence(List<string> haystack, List<string> needle, int startIndex, bool requireEOF)
    {
        if (needle.Count == 0)
        {
            return -1;
        }
        if (startIndex < 0)
        {
            startIndex = 0;
        }
        if (startIndex > haystack.Count)
        {
            startIndex = haystack.Count;
        }
        for (int i = startIndex; i <= haystack.Count - needle.Count; i++)
        {
            bool matched = true;
            for (int j = 0; j < needle.Count; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
            {
                if (requireEOF && !MatchSatisfiesEOF(haystack, i, needle.Count))
                {
                    continue;
                }
                return i;
            }
        }
        return -1;
    }

    internal static bool MatchSatisfiesEOF(List<string> lines, int start, int length)
    {
        var end = start + length;
        if (end >= lines.Count)
        {
            return true;
        }
        for (int i = end; i < lines.Count; i++)
        {
            if (lines[i] != "")
            {
                return false;
            }
        }
        return true;
    }

    internal static List<string> EnsureNormalizedLines(FileState state)
    {
        if (state == null)
        {
            return new List<string>();
        }
        if (!state.Options.IgnoreWhitespace)
        {
            return state.Lines;
        }
        if (state.NormalizedLines != null)
        {
            return state.NormalizedLines;
        }
        state.NormalizedLines = state.Lines.Select(NormalizeLine).ToList();
        return state.NormalizedLines;
    }

    internal static void UpdateNormalizedLines(FileState state, int index, int deleteCount, List<string> replacement)
    {
        if (state == null || !state.Options.IgnoreWhitespace)
        {
            return;
        }
        var normalized = EnsureNormalizedLines(state);
        var replacementNormalized = replacement.Select(NormalizeLine).ToList();
        state.NormalizedLines = Splice(normalized, index, deleteCount, replacementNormalized);
    }

    internal static string NormalizeLine(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return "";
        }
        var sb = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    internal static PatchException EnhanceHunkError(PatchException err, FileState state, Hunk hunk, int number)
    {
        var statuses = new List<HunkStatus>(state.HunkStatuses);
        if (err.HunkStatuses.Count > 0)
        {
            statuses.AddRange(err.HunkStatuses);
        }
        statuses.Add(new HunkStatus { Number = number, Status = "no-match" });
        err.HunkStatuses = statuses;

        if (string.IsNullOrEmpty(err.Code))
        {
            err.Code = "HUNK_NOT_FOUND";
        }
        if (string.IsNullOrEmpty(err.RelativePath) && state != null)
        {
            err.RelativePath = state.RelativePath;
        }
        if (string.IsNullOrEmpty(err.OriginalContent) && state != null)
        {
            err.OriginalContent = !string.IsNullOrEmpty(state.OriginalContent)
                ? state.OriginalContent
                : string.Join("\n", state.Lines);
        }
        if (err.FailedHunk == null)
        {
            err.FailedHunk = new FailedHunk
            {
                Number = number,
                RawPatchLines = new List<string>(hunk.RawPatchLines)
            };
        }
        return err;
    }

    internal static string DescribeHunkStatuses(List<HunkStatus> statuses)
    {
        if (statuses.Count == 0)
        {
            return "";
        }
        var applied = new List<string>();
        var failed = "";
        foreach (var status in statuses)
        {
            if (status.Status == "applied")
            {
                applied.Add(status.Number.ToString());
                continue;
            }
            if (string.IsNullOrEmpty(failed))
            {
                failed = $"No match for hunk {status.Number}.";
            }
        }

        var parts = new List<string>();
        if (applied.Count > 0)
        {
            parts.Add($"Hunks applied: {string.Join(", ", applied)}.");
        }
        if (!string.IsNullOrEmpty(failed))
        {
            parts.Add(failed);
        }
        return string.Join("\n", parts);
    }
}
