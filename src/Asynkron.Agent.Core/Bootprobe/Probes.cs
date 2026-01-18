using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Asynkron.Agent.Core.Bootprobe;

// Result BootProbeResult mirrors the structure returned by the upstream TypeScript
// implementation and captures the detected capabilities of the current project
// and execution environment.
public sealed class Result
{
    public NodeProbeResult? Node { get; init; }
    public PythonProbeResult? Python { get; init; }
    public SimpleProbeResult? DotNet { get; init; }
    public SimpleProbeResult? Go { get; init; }
    public RustProbeResult? Rust { get; init; }
    public JVMProbeResult? JVM { get; init; }
    public SimpleProbeResult? Git { get; init; }
    public List<ContainerProbeResult> Containers { get; init; } = [];
    public List<ToolingProbeResult> Linters { get; init; } = [];
    public List<ToolingProbeResult> Formatters { get; init; } = [];
    public OSResult OS { get; init; } = new();
    public ShellProbeResult Shell { get; init; } = new();

    // HasCapabilities reports whether any tooling was detected.
    public bool HasCapabilities() =>
        Node != null || Python != null || DotNet != null || Go != null || 
        Rust != null || JVM != null || Git != null || Containers.Count > 0 || 
        Linters.Count > 0 || Formatters.Count > 0;

    // SummaryLines returns the human-readable bullet lines describing the detected
    // capabilities.
    public List<string> SummaryLines()
    {
        var lines = new List<string>();

        if (Node != null)
        {
            lines.Add(Probes.FormatNodeSummary(Node));
        }
        if (Python != null)
        {
            lines.Add(Probes.FormatSimpleSummary("Python project", Python.Indicators, Python.Commands));
        }
        if (DotNet != null)
        {
            lines.Add(Probes.FormatSimpleSummary(".NET SDK", DotNet.Indicators, DotNet.Commands));
        }
        if (Go != null)
        {
            lines.Add(Probes.FormatSimpleSummary("Go toolchain", Go.Indicators, Go.Commands));
        }
        if (Rust != null)
        {
            lines.Add(Probes.FormatSimpleSummary("Rust toolchain", Rust.Indicators, Rust.Commands));
        }
        if (JVM != null)
        {
            lines.Add(Probes.FormatJVMSummary(JVM));
        }
        if (Git != null)
        {
            lines.Add(Probes.FormatSimpleSummary("Git repository", Git.Indicators, Git.Commands));
        }
        if (Containers.Count > 0)
        {
            foreach (var container in Containers)
            {
                lines.Add(Probes.FormatContainerSummary(container));
            }
        }
        if (Linters.Count > 0)
        {
            lines.Add(Probes.FormatToolSummary("Linters", Linters));
        }
        if (Formatters.Count > 0)
        {
            lines.Add(Probes.FormatToolSummary("Formatters", Formatters));
        }

        return lines;
    }
}

// CommandStatus records whether a particular command is available on PATH.
public sealed class CommandStatus
{
    public string Name { get; init; } = "";
    public bool Available { get; init; }
}

// SimpleProbeResult captures a boolean detection and supporting indicators for
// a tooling family.
public sealed class SimpleProbeResult
{
    public bool Detected { get; init; }
    public List<string> Indicators { get; init; } = [];
    public List<CommandStatus> Commands { get; init; } = [];
}

// NodeProbeResult captures information about a JavaScript/TypeScript project.
public sealed class NodeProbeResult
{
    public bool Detected { get; init; }
    public List<string> Indicators { get; init; } = [];
    public List<CommandStatus> Commands { get; init; } = [];
    public bool HasTypeScript { get; init; }
    public bool HasJavaScript { get; init; }
    public List<string> PackageManagers { get; init; } = [];
}

// PythonProbeResult captures Python specific metadata.
public sealed class PythonProbeResult
{
    public bool Detected { get; init; }
    public List<string> Indicators { get; init; } = [];
    public List<CommandStatus> Commands { get; init; } = [];
    public bool UsesPoetry { get; init; }
    public bool UsesPipenv { get; init; }
}

// RustProbeResult captures Rust specific metadata.
public sealed class RustProbeResult
{
    public bool Detected { get; init; }
    public List<string> Indicators { get; init; } = [];
    public List<CommandStatus> Commands { get; init; } = [];
}

// JVMProbeResult captures information about JVM build tooling.
public sealed class JVMProbeResult
{
    public bool Detected { get; init; }
    public List<string> Indicators { get; init; } = [];
    public List<CommandStatus> Commands { get; init; } = [];
    public List<string> BuildTools { get; init; } = [];
}

// ContainerProbeResult describes container configuration or tooling.
public sealed class ContainerProbeResult
{
    public bool Detected { get; init; }
    public List<string> Indicators { get; init; } = [];
    public List<CommandStatus> Commands { get; init; } = [];
    public string Runtime { get; init; } = "";
}

// ToolingProbeResult captures formatter or linter tools.
public sealed class ToolingProbeResult
{
    public string Name { get; init; } = "";
    public List<string> Indicators { get; init; } = [];
    public List<CommandStatus> Commands { get; init; } = [];
}

// OSResult summarises the host operating system and architecture.
public sealed class OSResult
{
    public string Platform { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string Distribution { get; init; } = "";
}

// ShellProbeResult summarises the user's shells.
// Default: the login shell configured for the account (e.g. zsh).
// Current: the parent process shell of the CLI invocation (e.g. zsh, bash, fish).
// Source: how Default was determined (dscl, getent, passwd, env).
public sealed class ShellProbeResult
{
    public string Default { get; set; } = "";
    public string Current { get; set; } = "";
    public string Source { get; set; } = "";
}

public static class Probes
{
    // Run executes all boot probes and returns a consolidated result structure.
    public static async Task<Result> Run(BootprobeContext ctx)
    {
        return new Result
        {
            Node = await RunNodeProbe(ctx),
            Python = await RunPythonProbe(ctx),
            DotNet = RunDotNetProbe(ctx),
            Go = await RunGoProbe(ctx),
            Rust = RunRustProbe(ctx),
            JVM = RunJVMProbe(ctx),
            Git = RunGitProbe(ctx),
            Containers = RunContainerProbes(ctx),
            Linters = await RunLintProbes(ctx),
            Formatters = await RunFormatterProbes(ctx),
            OS = DetectOS(),
            Shell = await DetectShell(ctx)
        };
    }

    private static async Task<NodeProbeResult?> RunNodeProbe(BootprobeContext ctx)
    {
        var indicators = CollectExistingFiles(ctx, [
            "package.json",
            "pnpm-workspace.yaml",
            "yarn.lock",
            "package-lock.json",
            "tsconfig.json",
            "tsconfig.base.json",
            "jsconfig.json"
        ]);

        var hasTSFile = false;
        var hasJSFile = false;
        var (_, tsfound) = ctx.FindFirstWithSuffix(".ts", ".tsx");
        if (tsfound)
        {
            hasTSFile = true;
        }
        var (_, jsfound) = ctx.FindFirstWithSuffix(".js", ".jsx", ".mjs", ".cjs");
        if (jsfound)
        {
            hasJSFile = true;
        }

        var commands = CommandStatuses(ctx, "node", "npm", "pnpm", "yarn", "npx");
        var pkgManagers = AvailableCommandNames(commands, "npm", "pnpm", "yarn");

        var detected = indicators.Count > 0 || hasTSFile || hasJSFile;
        if (!detected)
        {
            return null;
        }

        if (hasTSFile)
        {
            indicators.Add("TypeScript sources");
        }
        if (hasJSFile)
        {
            indicators.Add("JavaScript sources");
        }

        return new NodeProbeResult
        {
            Detected = true,
            Indicators = DedupeStrings(indicators),
            Commands = commands,
            HasTypeScript = hasTSFile,
            HasJavaScript = hasJSFile,
            PackageManagers = pkgManagers
        };
    }

    private static async Task<PythonProbeResult?> RunPythonProbe(BootprobeContext ctx)
    {
        var indicators = CollectExistingFiles(ctx, [
            "pyproject.toml",
            "requirements.txt",
            "requirements-dev.txt",
            "Pipfile",
            "setup.cfg",
            "setup.py",
            "environment.yml"
        ]);

        var usesPoetry = ctx.HasFile("poetry.lock");
        var usesPipenv = ctx.HasAnyFile("Pipfile", "Pipfile.lock");
        if (usesPoetry)
        {
            indicators.Add("poetry.lock");
        }
        if (usesPipenv)
        {
            indicators.Add("Pipenv files");
        }

        var commands = CommandStatuses(ctx, "python3", "python", "pip", "poetry", "pipenv");
        if (indicators.Count == 0)
        {
            return null;
        }

        return new PythonProbeResult
        {
            Detected = true,
            Indicators = DedupeStrings(indicators),
            Commands = commands,
            UsesPoetry = usesPoetry,
            UsesPipenv = usesPipenv
        };
    }

    private static SimpleProbeResult? RunDotNetProbe(BootprobeContext ctx)
    {
        var indicators = new List<string>();
        var (path, ok) = ctx.FindFirstWithSuffix(".csproj", ".fsproj");
        if (ok)
        {
            indicators.Add(Path.GetFileName(path));
        }
        if (ctx.HasAnyFile("global.json", "Directory.Build.props", "Directory.Build.targets"))
        {
            indicators.Add("SDK configuration");
        }
        var commands = CommandStatuses(ctx, "dotnet");
        if (indicators.Count == 0)
        {
            return null;
        }

        return new SimpleProbeResult
        {
            Detected = true,
            Indicators = DedupeStrings(indicators),
            Commands = commands
        };
    }

    private static async Task<SimpleProbeResult?> RunGoProbe(BootprobeContext ctx)
    {
        var indicators = CollectExistingFiles(ctx, ["go.mod", "go.sum", "go.work"]);
        // Check for common Go-related commands beyond just `go`.
        var commands = CommandStatuses(ctx, "go", "gofmt", "goimports", "golangci-lint", "staticcheck");
        if (indicators.Count == 0)
        {
            return null;
        }

        // Try to capture `go version`.
        if (ctx.CommandExists("go"))
        {
            var (output, err) = await ctx.RunCommandOutput("go", "version");
            if (err == null)
            {
                var ver = output.Trim();
                if (!string.IsNullOrEmpty(ver))
                {
                    indicators.Add("go version: " + ver);
                }
            }
        }

        // Parse toolchain directive from go.mod if present (Go 1.21+ feature).
        if (ctx.HasFile("go.mod"))
        {
            try
            {
                var data = await ctx.ReadFile("go.mod");
                var tc = ParseGoToolchain(Encoding.UTF8.GetString(data));
                if (!string.IsNullOrEmpty(tc))
                {
                    indicators.Add("toolchain: " + tc);
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        // Query `go env -json` for key environment values.
        if (ctx.CommandExists("go"))
        {
            var (output, err) = await ctx.RunCommandOutput("go", "env", "-json");
            if (err == null)
            {
                try
                {
                    var env = JsonSerializer.Deserialize<GoEnv>(output);
                    if (env != null)
                    {
                        if (!string.IsNullOrEmpty(env.GOROOT))
                        {
                            indicators.Add("GOROOT=" + env.GOROOT);
                        }
                        if (!string.IsNullOrEmpty(env.GOPATH))
                        {
                            indicators.Add("GOPATH=" + env.GOPATH);
                        }
                        if (!string.IsNullOrEmpty(env.GOMODCACHE))
                        {
                            indicators.Add("GOMODCACHE=" + env.GOMODCACHE);
                        }
                    }
                }
                catch
                {
                    // Ignore JSON errors
                }
            }
        }

        return new SimpleProbeResult
        {
            Detected = true,
            Indicators = DedupeStrings(indicators),
            Commands = commands
        };
    }

    private sealed class GoEnv
    {
        public string GOPATH { get; set; } = "";
        public string GOROOT { get; set; } = "";
        public string GOMODCACHE { get; set; } = "";
    }

    // parseGoToolchain extracts the value of the `toolchain` directive from a go.mod
    // file content. It returns an empty string if not present.
    private static string ParseGoToolchain(string modFile)
    {
        // A very small and robust parser: scan lines and look for a line starting
        // with "toolchain" followed by the toolchain string.
        // Examples:
        //   toolchain go1.22.3
        //   toolchain golang.org/toolchain@v0.0.1-go1.22.0
        if (string.IsNullOrEmpty(modFile))
        {
            return "";
        }
        var lines = modFile.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//"))
            {
                continue;
            }
            if (trimmed.StartsWith("toolchain "))
            {
                var value = trimmed.Substring("toolchain ".Length).Trim();
                // Strip optional trailing comments.
                var idx = value.IndexOfAny(['\t', ' ', '#']);
                if (idx >= 0)
                {
                    value = value.Substring(0, idx).Trim();
                }
                return value;
            }
        }
        return "";
    }

    private static RustProbeResult? RunRustProbe(BootprobeContext ctx)
    {
        var indicators = CollectExistingFiles(ctx, ["Cargo.toml", "Cargo.lock"]);
        var commands = CommandStatuses(ctx, "cargo", "rustc");
        if (indicators.Count == 0)
        {
            return null;
        }
        return new RustProbeResult
        {
            Detected = true,
            Indicators = DedupeStrings(indicators),
            Commands = commands
        };
    }

    private static JVMProbeResult? RunJVMProbe(BootprobeContext ctx)
    {
        var indicators = new List<string>();
        var buildTools = new List<string>();

        if (ctx.HasFile("pom.xml") || ctx.HasFile("pom.yaml"))
        {
            indicators.Add("Maven project");
            buildTools.Add("Maven");
        }
        if (ctx.HasFile("build.gradle") || ctx.HasFile("build.gradle.kts") || ctx.HasFile("settings.gradle"))
        {
            indicators.Add("Gradle project");
            buildTools.Add("Gradle");
        }
        if (ctx.HasFile("build.sbt"))
        {
            indicators.Add("SBT project");
            buildTools.Add("SBT");
        }
        var (path, ok) = ctx.FindFirstWithSuffix(".java", ".kt", ".scala");
        if (ok)
        {
            indicators.Add(Path.GetFileName(path));
        }
        var commands = CommandStatuses(ctx, "java", "javac", "mvn", "gradle", "gradlew", "sbt");
        if (indicators.Count == 0)
        {
            return null;
        }

        return new JVMProbeResult
        {
            Detected = true,
            Indicators = DedupeStrings(indicators),
            Commands = commands,
            BuildTools = DedupeStrings(buildTools)
        };
    }

    private static SimpleProbeResult? RunGitProbe(BootprobeContext ctx)
    {
        var indicators = new List<string>();
        if (ctx.HasDir(".git"))
        {
            indicators.Add(".git directory");
        }
        else
        {
            var (path, ok) = ctx.FindFirstFileNamed(".gitmodules");
            if (ok)
            {
                indicators.Add(Path.GetFileName(path));
            }
        }
        var commands = CommandStatuses(ctx, "git");
        if (indicators.Count == 0)
        {
            return null;
        }
        return new SimpleProbeResult
        {
            Detected = true,
            Indicators = DedupeStrings(indicators),
            Commands = commands
        };
    }

    private static List<ContainerProbeResult> RunContainerProbes(BootprobeContext ctx)
    {
        var results = new List<ContainerProbeResult>();

        var dockerIndicators = CollectExistingFiles(ctx, [
            "Dockerfile",
            "docker-compose.yml",
            "docker-compose.yaml",
            ".dockerignore"
        ]);
        var dockerCommands = CommandStatuses(ctx, "docker");
        if (dockerIndicators.Count > 0)
        {
            results.Add(new ContainerProbeResult
            {
                Detected = true,
                Indicators = DedupeStrings(dockerIndicators),
                Commands = dockerCommands,
                Runtime = "Docker"
            });
        }

        var podmanStatus = CommandStatuses(ctx, "podman");
        if (podmanStatus.Count > 0 && podmanStatus[0].Available)
        {
            results.Add(new ContainerProbeResult
            {
                Detected = true,
                Commands = podmanStatus,
                Runtime = "Podman"
            });
        }

        var nerdctlStatus = CommandStatuses(ctx, "nerdctl");
        if (nerdctlStatus.Count > 0 && nerdctlStatus[0].Available)
        {
            results.Add(new ContainerProbeResult
            {
                Detected = true,
                Commands = nerdctlStatus,
                Runtime = "nerdctl"
            });
        }

        return results;
    }

    private static async Task<List<ToolingProbeResult>> RunLintProbes(BootprobeContext ctx)
    {
        var results = new List<ToolingProbeResult>();

        var eslintIndicators = CollectExistingFiles(ctx, [
            ".eslintrc",
            ".eslintrc.json",
            ".eslintrc.js",
            ".eslintrc.cjs",
            ".eslintrc.yaml",
            ".eslintrc.yml"
        ]);
        if (eslintIndicators.Count > 0)
        {
            results.Add(new ToolingProbeResult
            {
                Name = "ESLint",
                Indicators = DedupeStrings(eslintIndicators),
                Commands = CommandStatuses(ctx, "eslint", "npx")
            });
        }

        if (ctx.HasFile("pyproject.toml"))
        {
            try
            {
                var content = await ctx.ReadFile("pyproject.toml");
                if (BytesContainsAny(content, ["[tool.flake8]", "[tool.ruff]"]))
                {
                    results.Add(new ToolingProbeResult
                    {
                        Name = "Python linters",
                        Indicators = ["pyproject.toml"],
                        Commands = CommandStatuses(ctx, "ruff", "flake8")
                    });
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        return results;
    }

    private static async Task<List<ToolingProbeResult>> RunFormatterProbes(BootprobeContext ctx)
    {
        var results = new List<ToolingProbeResult>();

        var prettierIndicators = CollectExistingFiles(ctx, [
            ".prettierrc",
            ".prettierrc.json",
            ".prettierrc.js",
            ".prettierrc.cjs",
            ".prettierrc.yaml",
            ".prettierrc.yml",
            "prettier.config.js",
            "prettier.config.cjs"
        ]);
        if (prettierIndicators.Count > 0)
        {
            results.Add(new ToolingProbeResult
            {
                Name = "Prettier",
                Indicators = DedupeStrings(prettierIndicators),
                Commands = CommandStatuses(ctx, "prettier", "npx")
            });
        }

        if (ctx.HasFile("pyproject.toml"))
        {
            try
            {
                var content = await ctx.ReadFile("pyproject.toml");
                if (BytesContainsAny(content, ["[tool.black]", "[tool.ruff.format]"]))
                {
                    results.Add(new ToolingProbeResult
                    {
                        Name = "Python formatters",
                        Indicators = ["pyproject.toml"],
                        Commands = CommandStatuses(ctx, "black", "ruff")
                    });
                }
            }
            catch
            {
                // Ignore errors
            }
        }

        if (ctx.HasFile(".clang-format"))
        {
            results.Add(new ToolingProbeResult
            {
                Name = "clang-format",
                Indicators = [".clang-format"],
                Commands = CommandStatuses(ctx, "clang-format")
            });
        }

        return results;
    }

    private static OSResult DetectOS()
    {
        return new OSResult
        {
            Platform = GetPlatform(),
            Architecture = GetArchitecture(),
            Distribution = ReadOSRelease()
        };
    }

    private static async Task<ShellProbeResult> DetectShell(BootprobeContext ctx)
    {
        var res = new ShellProbeResult();

        // Detect default login shell
        var username = Environment.UserName;
        
        // On macOS, prefer dscl for accuracy with Directory Services
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && ctx.CommandExists("dscl"))
        {
            var (output, err) = await ctx.RunCommandOutput("dscl", ".", "-read", "/Users/" + username, "UserShell");
            if (err == null)
            {
                // output like: "UserShell: /bin/zsh"
                var fields = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 2)
                {
                    res.Default = Basename(fields[^1]);
                    res.Source = "dscl";
                }
            }
        }
        
        // On Unix, getent passwd is canonical
        if (string.IsNullOrEmpty(res.Default) && ctx.CommandExists("getent"))
        {
            var (output, err) = await ctx.RunCommandOutput("getent", "passwd", username);
            if (err == null)
            {
                // colon separated; shell is field 7
                var parts = output.Trim().Split(':');
                if (parts.Length >= 7)
                {
                    res.Default = Basename(parts[6]);
                    res.Source = "getent";
                }
            }
        }
        
        // Fallback: parse /etc/passwd
        if (string.IsNullOrEmpty(res.Default))
        {
            try
            {
                var data = await File.ReadAllTextAsync("/etc/passwd");
                var lines = data.Split('\n');
                foreach (var line in lines)
                {
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    {
                        continue;
                    }
                    var parts = line.Split(':');
                    if (parts.Length >= 7 && parts[0] == username)
                    {
                        res.Default = Basename(parts[6]);
                        res.Source = "passwd";
                        break;
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
        }
        
        // Last resort: $SHELL
        if (string.IsNullOrEmpty(res.Default))
        {
            var sh = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrEmpty(sh))
            {
                res.Default = Basename(sh);
                res.Source = "env";
            }
        }

        // Detect current session shell (parent process command)
        var ppid = GetParentProcessId();
        if (ppid > 0 && ctx.CommandExists("ps"))
        {
            var (output, err) = await ctx.RunCommandOutput("ps", "-p", ppid.ToString(), "-o", "comm=");
            if (err == null)
            {
                var cur = output.Trim();
                if (!string.IsNullOrEmpty(cur))
                {
                    res.Current = Basename(cur);
                }
            }
        }
        if (string.IsNullOrEmpty(res.Current))
        {
            var sh = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrEmpty(sh))
            {
                res.Current = Basename(sh);
            }
        }

        return res;
    }

    private static string Basename(string path)
    {
        path = path.Trim();
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }
        // Avoid importing an extra package for a simple basename of paths/commands
        var idx = path.LastIndexOf('/');
        if (idx >= 0 && idx + 1 < path.Length)
        {
            return path.Substring(idx + 1);
        }
        return path;
    }

    private static List<string> CollectExistingFiles(BootprobeContext ctx, string[] files)
    {
        var results = new List<string>();
        foreach (var file in files)
        {
            if (ctx.HasFile(file))
            {
                results.Add(file);
            }
        }
        return results;
    }

    private static List<CommandStatus> CommandStatuses(BootprobeContext ctx, params string[] commands)
    {
        var statuses = new List<CommandStatus>();
        foreach (var cmd in commands)
        {
            statuses.Add(new CommandStatus
            {
                Name = cmd,
                Available = ctx.CommandExists(cmd)
            });
        }
        return statuses;
    }

    private static List<string> AvailableCommandNames(List<CommandStatus> commands, params string[] names)
    {
        var lookup = new HashSet<string>(names);
        var includeAll = names.Length == 0;
        var available = new List<string>();
        foreach (var cmd in commands)
        {
            if (!cmd.Available)
            {
                continue;
            }
            if (includeAll)
            {
                available.Add(cmd.Name);
                continue;
            }
            if (lookup.Contains(cmd.Name))
            {
                available.Add(cmd.Name);
            }
        }
        return available;
    }

    private static List<string> DedupeStrings(List<string> values)
        => values
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ReadOSRelease()
    {
        var candidates = new[] { "/etc/os-release", "/usr/lib/os-release" };
        foreach (var path in candidates)
        {
            try
            {
                var data = File.ReadAllText(path);
                var lower = data.ToLowerInvariant();
                var lines = lower.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    lines[i] = lines[i].Trim();
                }
                
                var originalLines = data.Split('\n');
                for (var idx = 0; idx < lines.Length; idx++)
                {
                    if (lines[idx].StartsWith("pretty_name="))
                    {
                        if (idx < originalLines.Length)
                        {
                            var value = originalLines[idx].Trim();
                            value = value.Substring("PRETTY_NAME=".Length);
                            value = value.Trim('"');
                            if (!string.IsNullOrEmpty(value))
                            {
                                return value;
                            }
                        }
                    }
                }
            }
            catch
            {
                continue;
            }
        }
        return "";
    }

    private static bool BytesContainsAny(byte[] data, string[] needles)
    {
        if (data.Length == 0)
        {
            return false;
        }
        var lowerData = Encoding.UTF8.GetString(data).ToLowerInvariant();
        foreach (var needle in needles)
        {
            if (string.IsNullOrEmpty(needle))
            {
                continue;
            }
            if (lowerData.Contains(needle.ToLowerInvariant()))
            {
                return true;
            }
        }
        return false;
    }

    private static string GetPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "windows";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "darwin";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
        {
            return "freebsd";
        }
        return "unknown";
    }

    private static string GetArchitecture()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.X86 => "386",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            _ => "unknown"
        };
    }

    private static int GetParentProcessId()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            // Unfortunately, .NET doesn't have a built-in way to get PPID
            // This is a workaround that works on Unix systems
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return 0; // Not easily available on Windows without native calls
            }
            
            // On Unix, we can read from /proc
            var pidPath = $"/proc/{process.Id}/status";
            if (File.Exists(pidPath))
            {
                var lines = File.ReadAllLines(pidPath);
                foreach (var line in lines)
                {
                    if (line.StartsWith("PPid:"))
                    {
                        var parts = line.Split(':', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && int.TryParse(parts[1].Trim(), out var ppid))
                        {
                            return ppid;
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return 0;
    }

    // FormatSummary renders a BootProbeResult into a human-readable summary. The
    // OS line is always included, followed by detected capabilities rendered as
    // bullet points.
    public static string FormatSummary(Result result)
    {
        var osLine = FormatOSLine(result.OS);
        var shellLine = FormatShellLine(result.Shell);
        var lines = result.SummaryLines();
        if (lines.Count == 0)
        {
            if (!string.IsNullOrEmpty(shellLine))
            {
                return osLine + "\n" + shellLine;
            }
            return osLine;
        }

        for (var i = 0; i < lines.Count; i++)
        {
            lines[i] = "- " + lines[i];
        }

        var header = new List<string> { osLine };
        if (!string.IsNullOrEmpty(shellLine))
        {
            header.Add(shellLine);
        }
        return string.Join("\n", header.Concat(lines));
    }

    // FormatShellLine renders a single line describing the user's shells.
    public static string FormatShellLine(ShellProbeResult shell)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(shell.Default))
        {
            parts.Add("default=" + shell.Default);
        }
        if (!string.IsNullOrEmpty(shell.Current))
        {
            parts.Add("current=" + shell.Current);
        }
        if (parts.Count == 0)
        {
            return "";
        }
        return "Shell: " + string.Join("; ", parts);
    }

    // FormatOSLine renders a single line describing the host OS.
    public static string FormatOSLine(OSResult osResult)
    {
        if (!string.IsNullOrEmpty(osResult.Distribution))
        {
            return $"OS: {osResult.Platform}/{osResult.Architecture} ({osResult.Distribution})";
        }
        return $"OS: {osResult.Platform}/{osResult.Architecture}";
    }

    public static string FormatNodeSummary(NodeProbeResult result)
    {
        var details = new List<string>();
        if (result.Indicators.Count > 0)
        {
            details.Add(string.Join(", ", result.Indicators));
        }
        if (result.PackageManagers.Count > 0)
        {
            details.Add("pkg mgrs: " + string.Join(", ", result.PackageManagers));
        }
        var available = AvailableCommandNames(result.Commands);
        if (available.Count > 0)
        {
            details.Add("commands: " + string.Join(", ", available));
        }
        return JoinSummary("Node.js project", details);
    }

    public static string FormatJVMSummary(JVMProbeResult result)
    {
        var details = new List<string>();
        if (result.Indicators.Count > 0)
        {
            details.Add(string.Join(", ", result.Indicators));
        }
        if (result.BuildTools.Count > 0)
        {
            details.Add("build: " + string.Join(", ", result.BuildTools));
        }
        var available = AvailableCommandNames(result.Commands);
        if (available.Count > 0)
        {
            details.Add("commands: " + string.Join(", ", available));
        }
        return JoinSummary("JVM tooling", details);
    }

    public static string FormatContainerSummary(ContainerProbeResult result)
    {
        var label = "Container tooling";
        if (!string.IsNullOrEmpty(result.Runtime))
        {
            label = result.Runtime + " tooling";
        }
        var details = new List<string>();
        if (result.Indicators.Count > 0)
        {
            details.Add(string.Join(", ", result.Indicators));
        }
        var available = AvailableCommandNames(result.Commands);
        if (available.Count > 0)
        {
            details.Add("commands: " + string.Join(", ", available));
        }
        return JoinSummary(label, details);
    }

    public static string FormatToolSummary(string category, List<ToolingProbeResult> tools)
    {
        var names = new List<string>();
        foreach (var tool in tools)
        {
            names.Add(tool.Name);
        }
        return $"{category}: {string.Join(", ", names)}";
    }

    public static string FormatSimpleSummary(string title, List<string> indicators, List<CommandStatus> commands)
    {
        var details = new List<string>();
        if (indicators.Count > 0)
        {
            details.Add(string.Join(", ", indicators));
        }
        var available = AvailableCommandNames(commands);
        if (available.Count > 0)
        {
            details.Add("commands: " + string.Join(", ", available));
        }
        return JoinSummary(title, details);
    }

    private static string JoinSummary(string title, List<string> details)
    {
        if (details.Count == 0)
        {
            return title;
        }
        return $"{title} ({string.Join("; ", details)})";
    }
}
