using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Asynkron.Agent.Core.Runtime;

internal sealed class TextWriterLoggerProvider : ILoggerProvider
{
    private readonly TextWriter _writer;
    private readonly LogLevel _minLevel;

    public TextWriterLoggerProvider(TextWriter writer, LogLevel minLevel)
    {
        _writer = writer ?? TextWriter.Null;
        _minLevel = minLevel;
    }

    public ILogger CreateLogger(string categoryName) => new TextWriterLogger(_writer, _minLevel, categoryName);

    public void Dispose()
    {
    }

    private sealed class TextWriterLogger : ILogger
    {
        private readonly TextWriter _writer;
        private readonly LogLevel _minLevel;
        private readonly string _category;

        public TextWriterLogger(TextWriter writer, LogLevel minLevel, string category)
        {
            _writer = writer;
            _minLevel = minLevel;
            _category = category;
        }

        IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

        bool ILogger.IsEnabled(LogLevel logLevel) => logLevel >= _minLevel;

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!((ILogger)this).IsEnabled(logLevel))
            {
                return;
            }

            if (formatter == null) throw new ArgumentNullException(nameof(formatter));

            var message = formatter(state, exception);
            var prefix = $"[{DateTime.UtcNow:O}] [{logLevel}] {_category}";
            if (!string.IsNullOrEmpty(message))
            {
                prefix += $": {message}";
            }
            if (exception != null)
            {
                prefix += $" Exception: {exception}";
            }

            _writer.WriteLine(prefix);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose()
            {
            }
        }
    }
}
