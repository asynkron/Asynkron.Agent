using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// LogLevel represents the severity of a log entry.
/// </summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error
}

/// <summary>
/// LogField represents a key-value pair in structured logging.
/// </summary>
public record LogField(string Key, object? Value);

/// <summary>
/// Logger provides structured logging capabilities with context support.
/// </summary>
public interface ILogger
{
    void Debug(string msg, params LogField[] fields);
    void Info(string msg, params LogField[] fields);
    void Warn(string msg, params LogField[] fields);
    void Error(string msg, Exception? err, params LogField[] fields);
    ILogger WithFields(params LogField[] fields);
}

/// <summary>
/// NoOpLogger is a logger that discards all log entries.
/// </summary>
public class NoOpLogger : ILogger
{
    public void Debug(string msg, params LogField[] fields) { }
    public void Info(string msg, params LogField[] fields) { }
    public void Warn(string msg, params LogField[] fields) { }
    public void Error(string msg, Exception? err, params LogField[] fields) { }
    public ILogger WithFields(params LogField[] fields) => this;
}

/// <summary>
/// StdLogger is a logger that writes structured log entries to a writer.
/// It includes trace IDs from context when available.
/// </summary>
public class StdLogger : ILogger
{
    private readonly List<LogField> _fields;
    private readonly LogLevel _minLevel;
    private readonly TextWriter _writer;

    public StdLogger(LogLevel minLevel, TextWriter? writer)
    {
        _fields = new List<LogField>();
        _minLevel = minLevel;
        _writer = writer ?? TextWriter.Null;
    }

    private StdLogger(List<LogField> fields, LogLevel minLevel, TextWriter writer)
    {
        _fields = fields;
        _minLevel = minLevel;
        _writer = writer;
    }

    private void Log(LogLevel level, string msg, Exception? err, params LogField[] fields)
    {
        if (!ShouldLog(level))
            return;

        var allFields = new List<LogField>(_fields);
        allFields.AddRange(fields);

        var parts = new List<string>
        {
            $"[{DateTime.Now:O}]",
            $"[{level.ToString().ToUpperInvariant()}]"
        };

        if (err != null)
        {
            parts.Add($"[error=\"{err.Message}\"]");
        }

        parts.Add(msg);

        if (allFields.Count > 0)
        {
            var fieldParts = allFields.Select(f => $"{f.Key}={f.Value}");
            parts.Add($"fields=[{string.Join(" ", fieldParts)}]");
        }

        _writer.WriteLine(string.Join(" ", parts));
    }

    private bool ShouldLog(LogLevel level)
    {
        return level >= _minLevel;
    }

    public void Debug(string msg, params LogField[] fields) => Log(LogLevel.Debug, msg, null, fields);
    public void Info(string msg, params LogField[] fields) => Log(LogLevel.Info, msg, null, fields);
    public void Warn(string msg, params LogField[] fields) => Log(LogLevel.Warn, msg, null, fields);
    public void Error(string msg, Exception? err, params LogField[] fields) => Log(LogLevel.Error, msg, err, fields);

    public ILogger WithFields(params LogField[] fields)
    {
        var newFields = new List<LogField>(_fields);
        newFields.AddRange(fields);
        return new StdLogger(newFields, _minLevel, _writer);
    }

    public TextWriter Writer => _writer;
}
