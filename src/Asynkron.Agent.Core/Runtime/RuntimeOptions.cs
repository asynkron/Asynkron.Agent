using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MSLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Asynkron.Agent.Core.Runtime;



/// <summary>
/// RuntimeOptions configures the runtime wrapper. It mirrors the top level
/// knobs exposed by the TypeScript runtime while keeping room for C# specific
/// ergonomics like injecting alternative readers or writers during tests.
/// </summary>
public sealed record RuntimeOptions
{
    private const string DefaultLoggerCategory = "Asynkron.Agent.Runtime";
    
    public string ApiKey { get; init; } = "";
    public string ApiBaseUrl { get; init; } = "";
    public string Model { get; init; } = "gpt-4.1";
    public string ReasoningEffort { get; init; } = "";
    public string SystemPromptAugment { get; init; } = "";
    public int AmnesiaAfterPasses { get; init; }
    public bool HandsFree { get; init; }
    public string HandsFreeTopic { get; init; } = "";
    public string HandsFreeAutoReply { get; init; } = "";
    public int MaxPasses { get; init; }
    public string? HistoryLogPath { get; init; } = "history.json";

    public int MaxContextTokens { get; init; } = 128000;
    public double CompactWhenPercent { get; init; } = 0.85;

    public int InputBuffer { get; init; } = 4;
    public int OutputBuffer { get; init; } = 16;

    public TextReader? InputReader { get; init; }
    public TextWriter? OutputWriter { get; init; }

    public bool DisableInputReader { get; init; }
    public bool DisableOutputForwarding { get; init; }

    public bool UseStreaming { get; init; } = true;

    public TimeSpan EmitTimeout { get; init; }

    public RetryConfig? ApiRetryConfig { get; init; }

    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public List<string> ExitCommands { get; init; } = ["exit", "quit", "/exit", "/quit"];

    public Dictionary<string, InternalCommandHandlerAsync> InternalCommands { get; init; } = new();

    public ILogger? Logger { get; init; }
    public string LogLevel { get; init; } = "Information";
    public string LogPath { get; init; } = "";
    public TextWriter? LogWriter { get; init; }

    /// <summary>
    /// Sets defaults for unspecified options
    /// </summary>
    public RuntimeOptions WithDefaults()
    {
        var opts = this;

        // Set model context budgets based on model name
        if (MaxContextTokens <= 0 || CompactWhenPercent <= 0)
        {
            if (ContextBudget.DefaultModelContextBudgets.TryGetValue(Model.ToLowerInvariant(), out var budget))
            {
                if (MaxContextTokens <= 0)
                    opts = opts with { MaxContextTokens = budget.MaxTokens };
                if (CompactWhenPercent <= 0)
                    opts = opts with { CompactWhenPercent = budget.CompactWhenPercent };
            }
        }

        if (opts.MaxContextTokens <= 0)
            opts = opts with { MaxContextTokens = 128000 };
        if (opts.CompactWhenPercent <= 0)
            opts = opts with { CompactWhenPercent = 0.85 };

        if (opts.InputReader == null)
            opts = opts with { InputReader = Console.In };
        if (opts.OutputWriter == null)
            opts = opts with { OutputWriter = Console.Out };

        if (opts.HandsFree && string.IsNullOrWhiteSpace(opts.HandsFreeTopic))
            opts = opts with { HandsFreeTopic = "Hands-free session" };

        // Set up logger if not provided
        if (opts.Logger == null)
        {
            TextWriter? writer = null;

            if (opts.LogWriter != null)
            {
                writer = opts.LogWriter;
            }
            else if (!string.IsNullOrWhiteSpace(opts.LogPath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(opts.LogPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    writer = File.AppendText(opts.LogPath);
                }
                catch
                {
                    // Silently fall back to NullLogger
                }
            }

            if (!Enum.TryParse<MSLogLevel>(opts.LogLevel, true, out var parsedLevel))
            {
                parsedLevel = MSLogLevel.Information;
            }

            if (writer == null)
            {
                opts = opts with { Logger = NullLogger.Instance };
            }
            else
            {
                var loggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.ClearProviders();
                    builder.SetMinimumLevel(parsedLevel);
                    builder.AddProvider(new TextWriterLoggerProvider(writer, parsedLevel));
                });

                opts = opts with
                {
                    Logger = loggerFactory.CreateLogger(DefaultLoggerCategory),
                    LogWriter = writer
                };
            }
        }

        return opts;
    }

    /// <summary>
    /// Validates that required options are set
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is required");
        }
    }
}
