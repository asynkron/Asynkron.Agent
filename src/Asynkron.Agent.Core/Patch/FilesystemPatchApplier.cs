namespace Asynkron.Agent.Core.Patch;

/// <summary>
/// FilesystemPatchApplier applies patch operations to the OS filesystem.
/// </summary>
public static class FilesystemPatchApplier
{
    /// <summary>
    /// ApplyFilesystemAsync applies operations to the OS filesystem.
    /// </summary>
    public static Task<List<PatchResult>> ApplyFilesystemAsync(
        CancellationToken cancellationToken,
        List<Operation> operations,
        FilesystemOptions opts)
    {
        var ws = new FilesystemWorkspace(opts);
        var results = PatchApplier.Apply(cancellationToken, operations, ws);
        return Task.FromResult(results);
    }

    /// <summary>
    /// ApplyFilesystemPatchAsync parses a raw patch payload and applies it to the filesystem.
    /// </summary>
    public static Task<List<PatchResult>> ApplyFilesystemPatchAsync(
        CancellationToken cancellationToken,
        string patchBody,
        FilesystemOptions opts)
    {
        var operations = PatchParser.Parse(patchBody);
        return ApplyFilesystemAsync(cancellationToken, operations, opts);
    }

    private class FilesystemWorkspace : PatchApplier.IWorkspace
    {
        private readonly PatchOptions _options;
        private readonly string _workingDir;
        private readonly Dictionary<string, PatchApplier.FileState> _states = new();
        private readonly List<PatchResult> _deletions = new();

        public FilesystemWorkspace(FilesystemOptions opts)
        {
            _options = opts.Options;
            var workingDir = opts.WorkingDir?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(workingDir))
            {
                workingDir = Directory.GetCurrentDirectory();
            }
            _workingDir = Path.GetFullPath(workingDir);
        }

        public PatchApplier.FileState Ensure(string path, bool create)
        {
            var (abs, rel) = ResolvePath(path);
            if (_states.TryGetValue(abs, out var state))
            {
                state.Options = _options;
                if (_options.IgnoreWhitespace)
                {
                    state.NormalizedLines = PatchApplier.EnsureNormalizedLines(state);
                }
                else
                {
                    state.NormalizedLines = null;
                }
                return state;
            }

            var exists = File.Exists(abs);
            var isDirectory = Directory.Exists(abs);

            if (exists && create)
            {
                if (isDirectory)
                {
                    throw new InvalidOperationException($"cannot add directory {rel}");
                }
                // Project semantics: treat add over existing as new. Start with
                // empty content and mark as new so status reports "A".
                state = new PatchApplier.FileState
                {
                    Path = abs,
                    RelativePath = rel,
                    Lines = new List<string>(),
                    Options = _options,
                    IsNew = true
                };
                if (_options.IgnoreWhitespace)
                {
                    state.NormalizedLines = new List<string>();
                }
                _states[abs] = state;
                return state;
            }
            else if (exists)
            {
                if (isDirectory)
                {
                    throw new InvalidOperationException($"cannot patch directory {rel}");
                }
                var content = File.ReadAllText(abs);
                var normalized = content.Replace("\r\n", "\n").Replace("\r", "\n");
                var lines = normalized.Split('\n').ToList();
                var ends = normalized.EndsWith("\n");
                state = new PatchApplier.FileState
                {
                    Path = abs,
                    RelativePath = rel,
                    Lines = lines,
                    OriginalContent = content,
                    OriginalEndsWithNewline = ends,
                    OriginalMode = GetFileMode(abs),
                    Options = _options
                };
                if (_options.IgnoreWhitespace)
                {
                    state.NormalizedLines = PatchApplier.EnsureNormalizedLines(state);
                }
                _states[abs] = state;
                return state;
            }
            else if (!create)
            {
                throw new InvalidOperationException($"failed to read {rel}: file does not exist");
            }
            else
            {
                state = new PatchApplier.FileState
                {
                    Path = abs,
                    RelativePath = rel,
                    Lines = new List<string>(),
                    Options = _options,
                    IsNew = true
                };
                if (_options.IgnoreWhitespace)
                {
                    state.NormalizedLines = new List<string>();
                }
                _states[abs] = state;
                return state;
            }
        }

        public void Delete(string path)
        {
            var (abs, rel) = ResolvePath(path);
            if (!File.Exists(abs) || Directory.Exists(abs))
            {
                throw new PatchException($"Failed to delete file {rel}");
            }
            File.Delete(abs);
            _deletions.Add(new PatchResult { Status = "D", Path = rel });
        }

        public List<PatchResult> Commit()
        {
            var results = new List<PatchResult>(_deletions);
            foreach (var (_, state) in _states)
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

                var writePath = state.Path;
                var displayPath = state.RelativePath;
                var moveTarget = state.MovePath?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(moveTarget))
                {
                    var (abs, rel) = ResolvePath(moveTarget);
                    writePath = abs;
                    displayPath = rel;
                }

                var dir = Path.GetDirectoryName(writePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var perm = state.OriginalMode;
                if (perm == 0)
                {
                    perm = OperatingSystem.IsWindows() ? 0 : 0x1A4; // 0644 octal
                }

                File.WriteAllText(writePath, newContent);

                // On Unix-like systems, restore permissions
                if (!OperatingSystem.IsWindows() && state.OriginalMode != 0)
                {
                    try
                    {
                        var info = new FileInfo(writePath);
                        // C# doesn't have direct chmod, but UnixFileMode is available in .NET 7+
                        // For compatibility, we'll skip detailed permission restoration
                        // The file system usually preserves reasonable defaults
                    }
                    catch
                    {
                        // Ignore permission restoration errors
                    }
                }

                if (!string.IsNullOrEmpty(moveTarget) && writePath != state.Path)
                {
                    if (File.Exists(state.Path))
                    {
                        File.Delete(state.Path);
                    }
                }

                var status = state.IsNew ? "A" : "M";
                results.Add(new PatchResult { Status = status, Path = displayPath });
            }
            return results;
        }

        private (string abs, string rel) ResolvePath(string relative)
        {
            var rel = relative?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(rel))
            {
                throw new InvalidOperationException("invalid patch path");
            }
            // Normalize the supplied path and force it to be treated relative to the workspace.
            var cleaned = Path.GetFullPath(Path.Combine(_workingDir, rel));
            // Ensure the resolved absolute path stays within the workspace directory.
            if (!cleaned.StartsWith(_workingDir + Path.DirectorySeparatorChar) && cleaned != _workingDir)
            {
                throw new InvalidOperationException($"invalid patch path outside workspace: {rel}");
            }
            var relPath = Path.GetRelativePath(_workingDir, cleaned);
            return (cleaned, relPath);
        }

        private static int GetFileMode(string path)
        {
            try
            {
                // On Windows, file modes don't apply in the same way
                if (OperatingSystem.IsWindows())
                {
                    return 0;
                }
                // For Unix-like systems, we would read the file mode
                // C# doesn't have direct stat() access without P/Invoke
                // Return 0 for now as the mode is preserved by the OS
                return 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
