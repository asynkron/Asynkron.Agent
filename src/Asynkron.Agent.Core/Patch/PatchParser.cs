namespace Asynkron.Agent.Core.Patch;

/// <summary>
/// PatchParser converts textual patch representations into structured operations.
/// </summary>
public static class PatchParser
{
    /// <summary>
    /// Parse converts the textual representation of a patch payload into a
    /// list of operations that can later be applied.
    /// </summary>
    public static List<Operation> Parse(string input)
    {
        var lines = SplitLines(input);
        var operations = new List<Operation>();
        Operation? currentOp = null;
        Hunk? currentHunk = null;
        bool inside = false;

        void FlushHunk()
        {
            if (currentHunk == null)
            {
                return;
            }
            if (currentOp == null)
            {
                throw new InvalidOperationException("hunk encountered before file directive");
            }
            var parsed = ParseHunk(currentHunk.Lines, currentOp.Path, currentHunk.Header);
            currentOp.Hunks.Add(parsed);
            currentHunk = null;
        }

        void FlushOp()
        {
            if (currentOp == null)
            {
                return;
            }
            FlushHunk();
            if (currentOp.Hunks.Count == 0 && (currentOp.Type != OperationType.Update || string.IsNullOrWhiteSpace(currentOp.MovePath)))
            {
                throw new InvalidOperationException($"no hunks provided for {currentOp.Path}");
            }
            operations.Add(currentOp);
            currentOp = null;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            switch (line)
            {
                case "*** Begin Patch":
                    inside = true;
                    continue;
                case "*** End Patch":
                    if (inside)
                    {
                        FlushOp();
                    }
                    inside = false;
                    continue;
            }

            if (!inside)
            {
                continue;
            }

            var trimmed = line.Trim();

            if (trimmed == "*** End of File")
            {
                if (currentOp == null)
                {
                    throw new InvalidOperationException("end-of-file marker encountered before a file directive");
                }
                if (currentHunk == null)
                {
                    currentHunk = new Hunk();
                }
                currentHunk.Lines.Add(line);
                continue;
            }

            if (trimmed.StartsWith("*** Move to: "))
            {
                if (currentOp == null)
                {
                    throw new InvalidOperationException("move directive encountered before a file directive");
                }
                if (currentOp.Type != OperationType.Update)
                {
                    throw new InvalidOperationException("move directive only allowed for update operations");
                }
                currentOp.MovePath = trimmed.Substring("*** Move to: ".Length).Trim();
                continue;
            }

            if (trimmed.StartsWith("*** Delete File: "))
            {
                FlushOp();
                var path = trimmed.Substring("*** Delete File: ".Length).Trim();
                operations.Add(new Operation { Type = OperationType.Delete, Path = path });
                currentOp = null;
                currentHunk = null;
                continue;
            }

            if (trimmed.StartsWith("*** "))
            {
                FlushOp();
                if (trimmed.StartsWith("*** Update File: "))
                {
                    var path = trimmed.Substring("*** Update File: ".Length).Trim();
                    currentOp = new Operation { Type = OperationType.Update, Path = path };
                    continue;
                }
                if (trimmed.StartsWith("*** Add File: "))
                {
                    var path = trimmed.Substring("*** Add File: ".Length).Trim();
                    currentOp = new Operation { Type = OperationType.Add, Path = path };
                    continue;
                }
                throw new InvalidOperationException($"unsupported patch directive: {line}");
            }

            if (currentOp == null)
            {
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }
                throw new InvalidOperationException($"diff content appeared before a file directive: \"{line}\"");
            }

            if (line.StartsWith("@@"))
            {
                FlushHunk();
                currentHunk = new Hunk { Header = line };
                continue;
            }

            if (currentHunk == null)
            {
                currentHunk = new Hunk();
            }
            currentHunk.Lines.Add(line);
        }

        if (inside)
        {
            throw new InvalidOperationException("missing *** End Patch terminator");
        }

        FlushOp();

        return operations;
    }

    private static Hunk ParseHunk(List<string> lines, string filePath, string header)
    {
        var hunk = new Hunk { Header = header };
        hunk.Lines.AddRange(lines);
        foreach (var raw in lines)
        {
            if (raw.StartsWith("+"))
            {
                hunk.After.Add(raw.Substring(1));
            }
            else if (raw.StartsWith("-"))
            {
                hunk.Before.Add(raw.Substring(1));
            }
            else if (raw.StartsWith(" "))
            {
                var value = raw.Substring(1);
                hunk.Before.Add(value);
                hunk.After.Add(value);
            }
            else if (raw.Trim() == "*** End of File")
            {
                hunk.AtEOF = true;
            }
            else if (raw == "\\ No newline at end of file")
            {
                // ignore marker
            }
            else
            {
                throw new InvalidOperationException($"unsupported hunk line in {filePath}: \"{raw}\"");
            }
        }
        if (!string.IsNullOrEmpty(header))
        {
            hunk.RawPatchLines.Add(header);
        }
        hunk.RawPatchLines.AddRange(lines);
        return hunk;
    }

    private static List<string> SplitLines(string input)
    {
        var normalized = input.Replace("\r\n", "\n").Replace("\r", "\n");
        return normalized.Split('\n').ToList();
    }
}
