namespace Asynkron.Agent.Core.Bootprobe;

// Context provides helper methods for inspecting the repository root and
// interrogating the current execution environment. The helpers are intentionally
// lightweight so that unit tests can supply fixture directories and a custom
// command lookup implementation.
public sealed class BootprobeContext(string root, Func<string, string?>? lookPath)
{
    private readonly Func<string, string?> _lookPath = lookPath ?? DefaultLookPath;

    // NewContext constructs a Context rooted at the provided path. Commands are
    // resolved using a default PATH lookup by default.
    public BootprobeContext(string root) : this(root, null)
    {
    }

    // NewContextWithLookPath allows tests to override the command lookup
    // implementation so that probes can be exercised without relying on tools being
    // present on the host PATH.

    // Root returns the root directory that probes should inspect.
    public string Root() => root;

    // HasFile reports whether a file exists relative to the repository root.
    public bool HasFile(string relPath)
    {
        if (string.IsNullOrEmpty(relPath))
        {
            return false;
        }
        var path = Path.Combine(root, relPath);
        try
        {
            var info = new FileInfo(path);
            return info.Exists && !info.Attributes.HasFlag(FileAttributes.Directory);
        }
        catch
        {
            return false;
        }
    }

    // HasDir reports whether a directory exists relative to the repository root.
    public bool HasDir(string relPath)
    {
        if (string.IsNullOrEmpty(relPath))
        {
            return false;
        }
        var path = Path.Combine(root, relPath);
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    // HasAnyFile returns true if any of the provided relative paths exist.
    public bool HasAnyFile(params string[] relPaths)
    {
        foreach (var rel in relPaths)
        {
            if (HasFile(rel))
            {
                return true;
            }
        }
        return false;
    }

    // ReadFile loads the contents of a project file relative to the repository
    // root.
    public async Task<byte[]> ReadFile(string relPath)
    {
        if (string.IsNullOrEmpty(relPath))
        {
            throw new ArgumentException("path must be provided");
        }
        return await File.ReadAllBytesAsync(Path.Combine(root, relPath));
    }

    // CommandExists reports whether a command is available on PATH.
    public bool CommandExists(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }
        return _lookPath(name) != null;
    }

    // RunCommandOutput resolves and executes a command, returning its combined
    // stdout/stderr output. Intended for lightweight, read-only probes such as
    // `go version` and `go env -json`.
    public async Task<(string output, Exception? error)> RunCommandOutput(string name, params string[] args)
    {
        if (string.IsNullOrEmpty(name))
        {
            return ("", new ArgumentException("command name must be provided"));
        }
        var path = _lookPath(name);
        if (path == null)
        {
            return ("", new FileNotFoundException($"command not found: {name}"));
        }

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = path;
            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.CreateNoWindow = true;

            var outputBuilder = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            return (outputBuilder.ToString(), null);
        }
        catch (Exception ex)
        {
            return ("", ex);
        }
    }

    // FindFirstWithSuffix walks the repository looking for a file with any of the
    // provided suffixes and returns the first match. The suffix comparison is
    // case-insensitive and should include the dot (e.g. ".csproj").
    public (string path, bool found) FindFirstWithSuffix(params string[] suffixes)
    {
        if (suffixes.Length == 0)
        {
            return ("", false);
        }

        var lowerSuffixes = new List<string>();
        foreach (var suffix in suffixes)
        {
            if (string.IsNullOrEmpty(suffix))
            {
                continue;
            }
            lowerSuffixes.Add(suffix.ToLowerInvariant());
        }

        if (lowerSuffixes.Count == 0)
        {
            return ("", false);
        }

        string? match = null;
        try
        {
            foreach (var path in EnumerateFiles(root))
            {
                var lower = Path.GetExtension(path).ToLowerInvariant();
                foreach (var suffix in lowerSuffixes)
                {
                    if (lower == suffix)
                    {
                        match = path;
                        return (match, true);
                    }
                }
            }
        }
        catch
        {
            return ("", false);
        }

        return (match ?? "", match != null);
    }

    // FindFirstFileNamed walks the repository and returns the first file whose name
    // exactly matches one of the provided candidates.
    public (string path, bool found) FindFirstFileNamed(params string[] names)
    {
        if (names.Length == 0)
        {
            return ("", false);
        }

        var normalized = new HashSet<string>();
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }
            normalized.Add(name.ToLowerInvariant());
        }

        if (normalized.Count == 0)
        {
            return ("", false);
        }

        string? match = null;
        try
        {
            foreach (var path in EnumerateFiles(root))
            {
                if (normalized.Contains(Path.GetFileName(path).ToLowerInvariant()))
                {
                    match = path;
                    return (match, true);
                }
            }
        }
        catch
        {
            return ("", false);
        }

        return (match ?? "", match != null);
    }

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            string[] files;
            string[] dirs;
            try
            {
                files = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                // Skip large dependency directories that do not affect the probes.
                if (name == "node_modules" || name == ".git" || name == "vendor" || name == "target")
                {
                    continue;
                }
                stack.Push(dir);
            }
        }
    }

    private static string? DefaultLookPath(string command)
    {
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT") ?? "";
        var extensions = pathExt.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var paths = Environment.GetEnvironmentVariable("PATH") ?? "";
        var pathDirs = paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        // On Unix, no extension is needed
        if (Environment.OSVersion.Platform == PlatformID.Unix || 
            Environment.OSVersion.Platform == PlatformID.MacOSX)
        {
            foreach (var dir in pathDirs)
            {
                var fullPath = Path.Combine(dir, command);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }
        else
        {
            // On Windows, try with and without extensions
            foreach (var dir in pathDirs)
            {
                var fullPath = Path.Combine(dir, command);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }

                foreach (var ext in extensions)
                {
                    var fullPathWithExt = fullPath + ext;
                    if (File.Exists(fullPathWithExt))
                    {
                        return fullPathWithExt;
                    }
                }
            }
        }

        return null;
    }
}
