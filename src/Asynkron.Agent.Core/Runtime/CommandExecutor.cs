using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// InternalCommandRequest represents a parsed internal command invocation.
/// </summary>
public class InternalCommandRequest
{
    /// <summary>
    /// Name is the normalized command identifier.
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Raw contains the original run string after trimming whitespace.
    /// </summary>
    public string Raw { get; set; } = string.Empty;
    
    /// <summary>
    /// Args stores named arguments (key=value pairs) parsed from the run string.
    /// </summary>
    public Dictionary<string, object> Args { get; set; } = new();
    
    /// <summary>
    /// Positionals stores ordered positional arguments parsed from the run string.
    /// </summary>
    public List<object> Positionals { get; set; } = new();
    
    /// <summary>
    /// Step contains the original plan step for reference.
    /// </summary>
    public PlanStep Step { get; set; } = new();
}

/// <summary>
/// InternalCommandHandler executes agent scoped commands that are not forwarded to the
/// host shell. Implementations can inspect the parsed arguments and return a
/// PlanObservationPayload describing the outcome.
/// </summary>
public delegate Task<PlanObservationPayload> InternalCommandHandlerAsync(InternalCommandRequest req, CancellationToken cancellationToken);

/// <summary>
/// CommandExecutor runs shell commands described by plan steps and also supports
/// a registry of agent internal commands that bypass the OS shell.
/// </summary>
public class CommandExecutor
{
    private const int MaxObservationBytes = 50 * 1024;
    private const string AgentShell = "openagent";
    
    private readonly Dictionary<string, InternalCommandHandlerAsync> _internal;
    private readonly ILogger _logger;
    private readonly IMetrics _metrics;
    
    /// <summary>
    /// NewCommandExecutor builds the default executor that shells out using Process.
    /// </summary>
    public CommandExecutor(ILogger? logger, IMetrics? metrics)
    {
        _internal = new Dictionary<string, InternalCommandHandlerAsync>(StringComparer.OrdinalIgnoreCase);
        _logger = logger ?? new NoOpLogger();
        _metrics = metrics ?? new NoOpMetrics();
    }
    
    /// <summary>
    /// RegisterInternalCommand installs a handler for the provided command name. Names are
    /// matched case-insensitively and must be non-empty.
    /// </summary>
    public void RegisterInternalCommand(string name, InternalCommandHandlerAsync handler)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("internal command: name must be non-empty", nameof(name));
        }
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler), "internal command: handler must not be null");
        }
        _internal[trimmed.ToLowerInvariant()] = handler;
    }
    
    /// <summary>
    /// Execute runs the provided command and returns stdout/stderr observations.
    /// </summary>
    public async Task<(PlanObservationPayload observation, Exception? err)> Execute(
        CancellationToken cancellationToken, 
        PlanStep step)
    {
        var start = DateTime.Now;
        _logger.Debug("Executing command",
            new LogField("step_id", step.Id),
            new LogField("shell", step.Command.Shell),
            new LogField("cwd", step.Command.Cwd)
        );
        
        if (string.IsNullOrWhiteSpace(step.Command.Shell) || string.IsNullOrWhiteSpace(step.Command.Run))
        {
            return (new PlanObservationPayload(), new Exception($"command: invalid shell or run for step {step.Id}"));
        }
        
        if (string.Equals(step.Command.Shell.Trim(), AgentShell, StringComparison.OrdinalIgnoreCase))
        {
            var (internalObs, err) = await ExecuteInternal(cancellationToken, step);
            var duration = DateTime.Now - start;
            _metrics.RecordCommandExecution(step.Id, duration, err == null);
            if (err != null)
            {
                _logger.Error("Internal command failed", err,
                    new LogField("step_id", step.Id),
                    new LogField("duration_ms", duration.TotalMilliseconds)
                );
            }
            else
            {
                _logger.Debug("Internal command completed",
                    new LogField("step_id", step.Id),
                    new LogField("duration_ms", duration.TotalMilliseconds)
                );
            }
            return (internalObs, err);
        }
        
        // Derive a timeout-scoped context before building the command so the Process
        // inherits the cancellation behavior directly.
        var timeout = TimeSpan.FromSeconds(step.Command.TimeoutSec);
        if (timeout <= TimeSpan.Zero)
        {
            timeout = TimeSpan.FromMinutes(1);
        }
        
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var runCtx = cts.Token;
        
        var (cmd, buildErr) = BuildShellCommand(step.Command.Shell, step.Command.Run);
        if (buildErr != null)
        {
            var duration = DateTime.Now - start;
            _metrics.RecordCommandExecution(step.Id, duration, false);
            _logger.Error("Failed to build command", buildErr,
                new LogField("step_id", step.Id)
            );
            return (new PlanObservationPayload(), new Exception($"command: {buildErr.Message}", buildErr));
        }
        
        if (!string.IsNullOrEmpty(step.Command.Cwd))
        {
            cmd!.StartInfo.WorkingDirectory = step.Command.Cwd;
        }
        
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        
        cmd!.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                stdoutBuilder.AppendLine(e.Data);
            }
        };
        
        cmd.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                stderrBuilder.AppendLine(e.Data);
            }
        };
        
        Exception? runErr = null;
        try
        {
            cmd.Start();
            cmd.BeginOutputReadLine();
            cmd.BeginErrorReadLine();
            
            await cmd.WaitForExitAsync(runCtx);
        }
        catch (OperationCanceledException)
        {
            if (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                runErr = new Exception($"command: timeout after {timeout}");
            }
            else
            {
                runErr = new OperationCanceledException();
            }
            
            try
            {
                if (!cmd.HasExited)
                {
                    cmd.Kill(true);
                }
            }
            catch
            {
                // Best effort
            }
        }
        catch (Exception ex)
        {
            runErr = ex;
        }
        
        var stdout = Encoding.UTF8.GetBytes(stdoutBuilder.ToString());
        var stderr = Encoding.UTF8.GetBytes(stderrBuilder.ToString());
        
        var filteredStdout = ApplyFilter(stdout, step.Command.FilterRegex);
        var filteredStderr = ApplyFilter(stderr, step.Command.FilterRegex);
        
        var (truncatedStdout, truncated) = TruncateOutput(filteredStdout, step.Command.MaxBytes, step.Command.TailLines);
        var (truncatedStderr, stderrTruncated) = TruncateOutput(filteredStderr, step.Command.MaxBytes, step.Command.TailLines);
        truncated = truncated || stderrTruncated;
        
        var observation = new PlanObservationPayload
        {
            Stdout = Encoding.UTF8.GetString(truncatedStdout),
            Stderr = Encoding.UTF8.GetString(truncatedStderr),
            Truncated = truncated
        };
        
        EnforceObservationLimit(ref observation);
        
        if (runErr == null)
        {
            observation.ExitCode = cmd.ExitCode;
        }
        else if (cmd.HasExited)
        {
            observation.ExitCode = cmd.ExitCode;
        }
        
        if (runErr != null && !cmd.HasExited)
        {
            observation.Details = runErr.Message;
        }
        
        var duration2 = DateTime.Now - start;
        
        // If the command failed, persist a detailed failure report for inspection.
        if (runErr != null)
        {
            var writeErr = await WriteFailureLog(step, stdout, stderr, runErr);
            if (writeErr != null)
            {
                // Log warning but don't fail execution - failure logging is best-effort
                _logger.Warn("Failed to write failure log",
                    new LogField("step_id", step.Id),
                    new LogField("error", writeErr.Message)
                );
            }
            _metrics.RecordCommandExecution(step.Id, duration2, false);
            _logger.Error("Command execution failed", runErr,
                new LogField("step_id", step.Id),
                new LogField("shell", step.Command.Shell),
                new LogField("duration_ms", duration2.TotalMilliseconds)
            );
            // Return error with step context
            if (!cmd.HasExited)
            {
                return (observation, new Exception($"command[{step.Id}]: execution failed: {runErr.Message}", runErr));
            }
            // Exit errors include exit code in the wrapped error
            return (observation, new Exception($"command[{step.Id}]: exited with code {observation.ExitCode}: {runErr.Message}", runErr));
        }
        
        _metrics.RecordCommandExecution(step.Id, duration2, true);
        _logger.Debug("Command execution completed",
            new LogField("step_id", step.Id),
            new LogField("duration_ms", duration2.TotalMilliseconds)
        );
        
        // Success - no error to return
        return (observation, null);
    }
    
    // writeFailureLog persists a diagnostic file under .goagent/ whenever a command
    // fails. The log captures the run string and the full, unfiltered stdout/stderr.
    // Any errors while writing the log are swallowed to avoid impacting the runtime.
    private static async Task<Exception?> WriteFailureLog(PlanStep step, byte[] fullStdout, byte[] fullStderr, Exception runErr)
    {
        // Resolve the base directory for logs. Prefer the step-specific Cwd when provided
        // so test invocations and sandboxed executions keep logs local to their workspace.
        var baseDir = step.Command.Cwd.Trim();
        if (string.IsNullOrEmpty(baseDir))
        {
            try
            {
                baseDir = Directory.GetCurrentDirectory();
            }
            catch
            {
                baseDir = ".";
            }
        }
        
        // Ensure target directory exists relative to the resolved base directory.
        var dir = Path.Combine(baseDir, ".goagent");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception err)
        {
            return err;
        }
        
        // Timestamped filename to avoid collisions.
        var filename = $"failure-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        var path = Path.Combine(dir, filename);
        
        // Compose a human-readable report. We intentionally include unfiltered,
        // untruncated outputs to aid debugging.
        var b = new StringBuilder();
        b.AppendLine($"Timestamp: {DateTime.Now:O}");
        b.AppendLine($"Shell: {step.Command.Shell}");
        b.AppendLine($"Cwd: {step.Command.Cwd}");
        b.AppendLine($"Run: {step.Command.Run}");
        if (step.Command.TimeoutSec > 0)
        {
            b.AppendLine($"TimeoutSec: {step.Command.TimeoutSec}");
        }
        if (!string.IsNullOrEmpty(step.Command.FilterRegex))
        {
            b.AppendLine($"FilterRegex: {step.Command.FilterRegex}");
        }
        if (step.Command.MaxBytes > 0)
        {
            b.AppendLine($"MaxBytes: {step.Command.MaxBytes}");
        }
        if (step.Command.TailLines > 0)
        {
            b.AppendLine($"TailLines: {step.Command.TailLines}");
        }
        if (runErr != null)
        {
            b.AppendLine($"Error: {runErr}");
        }
        if (!string.IsNullOrEmpty(step.Id))
        {
            b.AppendLine($"StepID: {step.Id}");
        }
        b.AppendLine();
        b.AppendLine("===== STDOUT (raw) =====");
        b.Append(Encoding.UTF8.GetString(fullStdout));
        if (fullStdout.Length > 0 && fullStdout[fullStdout.Length - 1] != '\n')
        {
            b.AppendLine();
        }
        b.AppendLine("===== STDERR (raw) =====");
        b.Append(Encoding.UTF8.GetString(fullStderr));
        if (fullStderr.Length > 0 && fullStderr[fullStderr.Length - 1] != '\n')
        {
            b.AppendLine();
        }
        
        try
        {
            await File.WriteAllTextAsync(path, b.ToString());
            return null;
        }
        catch (Exception err)
        {
            return new Exception($"writeFailureLog: failed to write file \"{path}\": {err.Message}", err);
        }
    }
    
    private async Task<(PlanObservationPayload observation, Exception? err)> ExecuteInternal(
        CancellationToken cancellationToken, 
        PlanStep step)
    {
        var (invocation, err) = ParseInternalInvocation(step);
        if (err != null)
        {
            _logger.Error("Failed to parse internal command invocation", err,
                new LogField("step_id", step.Id),
                new LogField("command_run", step.Command.Run)
            );
            return (new PlanObservationPayload(), new Exception($"command[{step.Id}]: parse internal invocation: {err.Message}", err));
        }
        
        if (!_internal.TryGetValue(invocation.Name, out var handler))
        {
            _logger.Error("Unknown internal command", null,
                new LogField("step_id", step.Id),
                new LogField("command_name", invocation.Name)
            );
            return (new PlanObservationPayload(), new Exception($"command[{step.Id}]: unknown internal command \"{invocation.Name}\""));
        }
        
        PlanObservationPayload payload;
        Exception? execErr;
        try
        {
            payload = await handler(invocation, cancellationToken);
            execErr = null;
        }
        catch (Exception ex)
        {
            payload = new PlanObservationPayload();
            execErr = ex;
        }
        
        if (execErr != null)
        {
            _logger.Error("Internal command execution failed", execErr,
                new LogField("step_id", step.Id),
                new LogField("command_name", invocation.Name)
            );
            if (string.IsNullOrEmpty(payload.Details))
            {
                payload.Details = execErr.Message;
            }
            return (payload, new Exception($"command[{step.Id}]: internal command \"{invocation.Name}\" failed: {execErr.Message}", execErr));
        }
        if (!payload.ExitCode.HasValue)
        {
            payload.ExitCode = 0;
        }
        return (payload, null);
    }
    
    private static (InternalCommandRequest invocation, Exception? err) ParseInternalInvocation(PlanStep step)
    {
        var run = step.Command.Run.Trim();
        var (tokens, err) = TokenizeInternalCommand(run);
        if (err != null)
        {
            return (new InternalCommandRequest(), new Exception($"parse internal command \"{run}\": {err.Message}", err));
        }
        if (tokens.Count == 0)
        {
            return (new InternalCommandRequest(), new Exception("internal command: missing command name"));
        }
        
        var name = tokens[0].ToLowerInvariant();
        var args = new Dictionary<string, object>();
        var positionals = new List<object>();
        
        for (int i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var parts = token.Split(new[] { '=' }, 2);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                args[parts[0].Trim()] = ParseInternalValue(parts[1]);
                continue;
            }
            positionals.Add(ParseInternalValue(token));
        }
        
        return (new InternalCommandRequest
        {
            Name = name,
            Raw = run,
            Args = args,
            Positionals = positionals,
            Step = step
        }, null);
    }
    
    internal static (List<string> tokens, Exception? err) TokenizeInternalCommand(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        bool escape = false;
        
        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        
        foreach (var r in input)
        {
            if (escape)
            {
                current.Append(r);
                escape = false;
            }
            else if (r == '\\')
            {
                escape = true;
            }
            else if (quote != '\0')
            {
                if (r == quote)
                {
                    quote = '\0';
                    continue;
                }
                current.Append(r);
            }
            else if (r == '\'' || r == '"')
            {
                quote = r;
            }
            else if (char.IsWhiteSpace(r))
            {
                Flush();
            }
            else
            {
                current.Append(r);
            }
        }
        
        if (escape)
        {
            return (new List<string>(), new Exception("internal command: unfinished escape sequence"));
        }
        if (quote != '\0')
        {
            return (new List<string>(), new Exception("internal command: unmatched quote"));
        }
        Flush();
        return (tokens, null);
    }
    
    private static object ParseInternalValue(string raw)
    {
        var trimmed = raw.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "";
        }
        
        var lower = trimmed.ToLowerInvariant();
        if (lower == "true")
        {
            return true;
        }
        if (lower == "false")
        {
            return false;
        }
        
        if (long.TryParse(trimmed, out var i))
        {
            return i;
        }
        if (double.TryParse(trimmed, out var f))
        {
            return f;
        }
        
        return trimmed;
    }
    
    private static byte[] ApplyFilter(byte[] output, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return output;
        }
        
        Regex rx;
        try
        {
            rx = new Regex(pattern);
        }
        catch
        {
            return output;
        }
        
        var text = Encoding.UTF8.GetString(output);
        var lines = text.Split('\n');
        var kept = new List<string>();
        foreach (var line in lines)
        {
            if (rx.IsMatch(line))
            {
                kept.Add(line);
            }
        }
        return Encoding.UTF8.GetBytes(string.Join("\n", kept));
    }
    
    private static (byte[] output, bool truncated) TruncateOutput(byte[] output, int maxBytes, int tailLines)
    {
        if (output.Length == 0)
        {
            return (output, false);
        }
        var truncated = false;
        if (maxBytes > 0 && output.Length > maxBytes)
        {
            output = output[(output.Length - maxBytes)..];
            truncated = true;
        }
        
        if (tailLines <= 0)
        {
            return (output, truncated);
        }
        
        var text = Encoding.UTF8.GetString(output);
        var lines = text.Split('\n');
        if (lines.Length > tailLines)
        {
            lines = lines[(lines.Length - tailLines)..];
            truncated = true;
        }
        
        return (Encoding.UTF8.GetBytes(string.Join("\n", lines)), truncated);
    }
    
    public static void EnforceObservationLimit(ref PlanObservationPayload payload)
    {
        if (payload == null)
        {
            return;
        }
        
        static (string value, bool truncated) TrimBuffer(string value)
        {
            if (value.Length <= MaxObservationBytes)
            {
                return (value, false);
            }
            return (value[(value.Length - MaxObservationBytes)..], true);
        }
        
        var (trimmedStdout, stdoutTruncated) = TrimBuffer(payload.Stdout);
        if (stdoutTruncated)
        {
            payload.Stdout = trimmedStdout;
            payload.Truncated = true;
        }
        
        var (trimmedStderr, stderrTruncated) = TrimBuffer(payload.Stderr);
        if (stderrTruncated)
        {
            payload.Stderr = trimmedStderr;
            payload.Truncated = true;
        }
        
        if (payload.PlanObservation != null)
        {
            for (int i = 0; i < payload.PlanObservation.Count; i++)
            {
                var entry = payload.PlanObservation[i];
                var (entryStdout, entryStdoutTrunc) = TrimBuffer(entry.Stdout);
                if (entryStdoutTrunc)
                {
                    entry.Stdout = entryStdout;
                    entry.Truncated = true;
                    payload.Truncated = true;
                }
                
                var (entryStderr, entryStderrTrunc) = TrimBuffer(entry.Stderr);
                if (entryStderrTrunc)
                {
                    entry.Stderr = entryStderr;
                    entry.Truncated = true;
                    payload.Truncated = true;
                }
            }
        }
    }
    
    // buildShellCommand normalizes the shell string ("/bin/bash", "bash -lc", etc.)
    // before wiring it up with the user's command. Supporting embedded flags lets
    // us accept both shorthand forms like "bash" and explicit "/bin/bash -lc" strings
    // returned by the assistant without failing at exec time.
    private static (Process? cmd, Exception? err) BuildShellCommand(string shell, string run)
    {
        var parts = shell.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return (null, new Exception($"invalid shell: \"{shell}\""));
        }
        
        var execPath = parts[0];
        var args = parts.Length > 1 ? parts[1..].ToList() : new List<string>();
        if (args.Count == 0)
        {
            args.Add("-lc");
        }
        
        args.Add(run);
        
        var psi = new ProcessStartInfo
        {
            FileName = execPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        
        return (new Process { StartInfo = psi }, null);
    }
    
    /// <summary>
    /// BuildToolMessage marshals the observation into a JSON string ready for tool messages.
    /// </summary>
    public static (string result, Exception? err) BuildToolMessage(PlanObservationPayload observation)
    {
        try
        {
            var json = JsonSerializer.Serialize(observation, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            var result = json.Trim();
            if (string.IsNullOrEmpty(result))
            {
                result = "{}";
            }
            return (result, null);
        }
        catch (Exception err)
        {
            return ("", err);
        }
    }
}
