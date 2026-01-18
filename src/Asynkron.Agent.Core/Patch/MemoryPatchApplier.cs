namespace Asynkron.Agent.Core.Patch;

/// <summary>
/// MemoryPatchApplier applies patch operations to an in-memory document store.
/// </summary>
public static class MemoryPatchApplier
{
    /// <summary>
    /// ApplyToMemoryAsync applies operations to an in-memory document store represented by a dictionary.
    /// The provided dictionary is copied before mutation and the updated snapshot is returned.
    /// </summary>
    public static Task<(Dictionary<string, string> files, List<PatchResult> results)> ApplyToMemoryAsync(
        CancellationToken cancellationToken,
        List<Operation> operations,
        Dictionary<string, string> files,
        PatchOptions opts)
    {
        var snapshot = new Dictionary<string, string>(files);
        var ws = new MemoryWorkspace(snapshot, opts);
        var results = PatchApplier.Apply(cancellationToken, operations, ws);
        return Task.FromResult((ws.Files, results));
    }

    /// <summary>
    /// ApplyMemoryPatchAsync parses a raw patch payload and applies it to an in-memory map of files.
    /// </summary>
    public static Task<(Dictionary<string, string> files, List<PatchResult> results)> ApplyMemoryPatchAsync(
        CancellationToken cancellationToken,
        string patchBody,
        Dictionary<string, string> files,
        PatchOptions opts)
    {
        var operations = PatchParser.Parse(patchBody);
        return ApplyToMemoryAsync(cancellationToken, operations, files, opts);
    }

    private sealed class MemoryWorkspace(Dictionary<string, string> files, PatchOptions opts) : PatchApplier.IWorkspace
    {
        private readonly Dictionary<string, PatchApplier.FileState> _states = new();
        private readonly List<PatchResult> _deletions = [];

        public Dictionary<string, string> Files { get; } = files;

        public PatchApplier.FileState Ensure(string path, bool create)
        {
            var rel = Path.GetFullPath(path.Trim()).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(rel) || rel == ".")
            {
                throw new InvalidOperationException("invalid patch path");
            }
            if (_states.TryGetValue(rel, out var state))
            {
                state.Options = opts;
                if (opts.IgnoreWhitespace)
                {
                    state.NormalizedLines = PatchApplier.EnsureNormalizedLines(state);
                }
                else
                {
                    state.NormalizedLines = null;
                }
                return state;
            }

            if (!Files.TryGetValue(rel, out var content))
            {
                if (!create)
                {
                    throw new InvalidOperationException($"failed to read {rel}: file does not exist");
                }
                state = new PatchApplier.FileState
                {
                    Path = rel,
                    RelativePath = rel,
                    Lines = [],
                    Options = opts,
                    IsNew = true
                };
                if (opts.IgnoreWhitespace)
                {
                    state.NormalizedLines = [];
                }
                _states[rel] = state;
                return state;
            }

            var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n').ToList();
            var ends = normalized.EndsWith("\n");
            state = new PatchApplier.FileState
            {
                Path = rel,
                RelativePath = rel,
                Lines = lines,
                OriginalContent = content,
                OriginalEndsWithNewline = ends,
                Options = opts
            };
            if (opts.IgnoreWhitespace)
            {
                state.NormalizedLines = PatchApplier.EnsureNormalizedLines(state);
            }
            _states[rel] = state;
            return state;
        }

        public void Delete(string path)
        {
            var rel = Path.GetFullPath(path.Trim()).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(rel) || rel == ".")
            {
                throw new InvalidOperationException("invalid patch path");
            }
            if (!Files.ContainsKey(rel))
            {
                throw new PatchException($"Failed to delete file {rel}");
            }
            Files.Remove(rel);
            _states.Remove(rel);
            _deletions.Add(new PatchResult { Status = "D", Path = rel });
        }

        public List<PatchResult> Commit()
        {
            var results = new List<PatchResult>(_deletions);
            foreach (var (key, state) in _states)
            {
                if (!state.Touched)
                {
                    continue;
                }
                var newContent = string.Join("\n", state.Lines);
                if (state.OriginalEndsWithNewline.HasValue)
                {
                    if (state.OriginalEndsWithNewline.Value && !newContent.EndsWith("\n"))
                    {
                        newContent += "\n";
                    }
                    if (!state.OriginalEndsWithNewline.Value && newContent.EndsWith("\n"))
                    {
                        newContent = newContent.TrimEnd('\n');
                    }
                }

                var writeKey = key;
                var display = state.RelativePath;
                var moveTarget = state.MovePath?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(moveTarget))
                {
                    var cleaned = Path.GetFullPath(moveTarget).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.IsNullOrEmpty(cleaned) || cleaned == ".")
                    {
                        throw new InvalidOperationException("invalid patch path");
                    }
                    writeKey = cleaned;
                    display = cleaned;
                }

                Files[writeKey] = newContent;
                if (!string.IsNullOrEmpty(moveTarget) && writeKey != key)
                {
                    Files.Remove(key);
                }

                var status = state.IsNew ? "A" : "M";
                results.Add(new PatchResult { Status = status, Path = display });
            }
            return results;
        }
    }
}
