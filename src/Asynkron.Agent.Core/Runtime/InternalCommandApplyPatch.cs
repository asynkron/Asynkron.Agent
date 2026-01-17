using System.Text;
using Asynkron.Agent.Core.Patch;

namespace Asynkron.Agent.Core.Runtime;

internal static class InternalCommandApplyPatch
{
    internal const string ApplyPatchCommandName = "apply_patch";

    internal static InternalCommandHandlerAsync NewApplyPatchCommand()
    {
        return async (req, cancellationToken) =>
        {
            var payload = new PlanObservationPayload();

            var (commandLine, patchInput) = SplitCommandAndPatch(req.Raw);
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return FailApplyPatch(ref payload, "internal command: apply_patch requires a command line");
            }

            var (opts, err) = ParseApplyPatchOptions(commandLine, req.Step.Command.Cwd);
            if (err != null)
            {
                return FailApplyPatch(ref payload, err.Message);
            }

            if (string.IsNullOrWhiteSpace(patchInput))
            {
                return FailApplyPatch(ref payload, "apply_patch: no patch provided");
            }

            List<Operation> operations;
            try
            {
                operations = PatchParser.Parse(patchInput);
            }
            catch (Exception parseEx)
            {
                var message = $"apply_patch: {parseEx.Message}";
                return FailApplyPatch(ref payload, message);
            }

            if (operations.Count == 0)
            {
                return FailApplyPatch(ref payload, "apply_patch: no patch operations detected");
            }

            List<PatchResult> results;
            try
            {
                results = await FilesystemPatchApplier.ApplyFilesystemAsync(cancellationToken, operations, opts);
            }
            catch (PatchException perr)
            {
                var formatted = PatchException.FormatError(perr);
                return FailApplyPatch(ref payload, formatted);
            }
            catch (Exception applyErr)
            {
                return FailApplyPatch(ref payload, applyErr.Message);
            }

            if (results.Count == 0)
            {
                payload.Stdout = "No changes applied.";
                payload.ExitCode = 0;
                return payload;
            }

            results.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.Ordinal));

            var builder = new StringBuilder();
            builder.AppendLine("Success. Updated the following files:");
            foreach (var entry in results)
            {
                builder.Append(entry.Status);
                builder.Append(' ');
                builder.AppendLine(entry.Path);
            }

            payload.Stdout = builder.ToString().TrimEnd('\n');
            payload.ExitCode = 0;
            return payload;
        };
    }

    private static PlanObservationPayload FailApplyPatch(ref PlanObservationPayload payload, string message)
    {
        payload.Stderr = message;
        payload.Details = message;
        payload.ExitCode = 1;
        return payload;
    }

    private static (string commandLine, string patch) SplitCommandAndPatch(string raw)
    {
        var trimmed = raw.TrimStart();
        if (string.IsNullOrEmpty(trimmed))
        {
            return ("", "");
        }
        
        var newlineIndex = trimmed.IndexOf('\n');
        if (newlineIndex == -1)
        {
            return (trimmed, "");
        }
        
        var line = trimmed.Substring(0, newlineIndex);
        var rest = trimmed.Substring(newlineIndex + 1);
        return (line, rest);
    }

    private static (FilesystemOptions opts, Exception? err) ParseApplyPatchOptions(string commandLine, string cwd)
    {
        var (tokens, err) = CommandExecutor.TokenizeInternalCommand(commandLine);
        if (err != null)
        {
            return (new FilesystemOptions(), new Exception($"failed to parse command line: {err.Message}", err));
        }
        if (tokens.Count == 0)
        {
            return (new FilesystemOptions(), new Exception("apply_patch: missing command name"));
        }

        var workingDir = cwd.Trim();
        if (string.IsNullOrEmpty(workingDir))
        {
            try
            {
                workingDir = Directory.GetCurrentDirectory();
            }
            catch (Exception getErr)
            {
                return (new FilesystemOptions(), new Exception($"failed to determine working directory: {getErr.Message}", getErr));
            }
        }
        
        try
        {
            workingDir = Path.GetFullPath(workingDir);
        }
        catch
        {
            // Keep original if GetFullPath fails
        }

        var opts = new FilesystemOptions
        {
            Options = new PatchOptions { IgnoreWhitespace = true },
            WorkingDir = workingDir
        };

        for (int i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var eqIndex = token.IndexOf('=');
            if (eqIndex != -1)
            {
                var key = token.Substring(0, eqIndex).Trim();
                var value = token.Substring(eqIndex + 1).Trim();
                var lowerKey = key.ToLowerInvariant();
                switch (lowerKey)
                {
                    case "ignore_whitespace":
                    case "ignore-whitespace":
                        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                        {
                            opts.Options.IgnoreWhitespace = false;
                        }
                        else if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                        {
                            opts.Options.IgnoreWhitespace = true;
                        }
                        break;
                    case "respect_whitespace":
                    case "respect-whitespace":
                        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                        {
                            opts.Options.IgnoreWhitespace = false;
                        }
                        break;
                }
                continue;
            }

            var lowerToken = token.ToLowerInvariant();
            switch (token)
            {
                case "--ignore-whitespace":
                case "-w":
                    opts.Options.IgnoreWhitespace = true;
                    break;
                case "--respect-whitespace":
                case "--no-ignore-whitespace":
                case "-W":
                    opts.Options.IgnoreWhitespace = false;
                    break;
                default:
                    switch (lowerToken)
                    {
                        case "--respect-whitespace":
                        case "--no-ignore-whitespace":
                            opts.Options.IgnoreWhitespace = false;
                            break;
                        case "--ignore-whitespace":
                            opts.Options.IgnoreWhitespace = true;
                            break;
                    }
                    break;
            }
        }
        
        return (opts, null);
    }
}
