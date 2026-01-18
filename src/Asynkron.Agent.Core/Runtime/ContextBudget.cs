using System;
using System.Collections.Generic;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// ContextBudget tracks the conversational budget for the runtime. The
/// compactor triggers once the estimated usage crosses the configured
/// percentage of the available tokens.
/// </summary>
public sealed record ContextBudget
{
    public int MaxTokens { get; init; }
    public double CompactWhenPercent { get; init; }

    private double NormalizedPercent()
    {
        var percent = CompactWhenPercent;
        if (percent > 1)
        {
            percent = percent / 100;
        }
        if (percent < 0)
        {
            percent = 0;
        }
        if (percent > 1)
        {
            percent = 1;
        }
        return percent;
    }

    public int TriggerTokens()
    {
        if (MaxTokens <= 0)
        {
            return 0;
        }
        var percent = NormalizedPercent();
        if (percent <= 0)
        {
            return 0;
        }
        var threshold = (int)Math.Ceiling(percent * MaxTokens);
        if (threshold < 1)
        {
            threshold = 1;
        }
        if (threshold > MaxTokens)
        {
            threshold = MaxTokens;
        }
        return threshold;
    }

    public static readonly Dictionary<string, ContextBudget> DefaultModelContextBudgets = new()
    {
        ["gpt-4.1"] = new ContextBudget { MaxTokens = 128000, CompactWhenPercent = 0.85 },
        ["gpt-4.1-mini"] = new ContextBudget { MaxTokens = 64000, CompactWhenPercent = 0.85 },
        ["gpt-4.1-nano"] = new ContextBudget { MaxTokens = 32000, CompactWhenPercent = 0.85 },
        ["gpt-4o"] = new ContextBudget { MaxTokens = 128000, CompactWhenPercent = 0.85 },
        ["gpt-4o-mini"] = new ContextBudget { MaxTokens = 64000, CompactWhenPercent = 0.85 },
        ["o1"] = new ContextBudget { MaxTokens = 128000, CompactWhenPercent = 0.8 },
        ["o1-preview"] = new ContextBudget { MaxTokens = 128000, CompactWhenPercent = 0.8 },
        ["o1-mini"] = new ContextBudget { MaxTokens = 64000, CompactWhenPercent = 0.8 }
    };
}
