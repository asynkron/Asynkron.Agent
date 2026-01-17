// Package bootprobe detects host/project capabilities to augment the system prompt.
namespace Asynkron.Agent.Core.Bootprobe;

public static class Bootstrap
{
    // BuildAugmentation runs the boot probe suite for the provided context and
    // returns the structured result, the formatted summary, and the combined
    // augmentation string that should be forwarded to the runtime. Keeping this
    // helper in the bootprobe package means callers can import it from a single
    // place without having to remember to compile additional files manually.
    public static async Task<(Result result, string summary, string combined)> BuildAugmentation(
        BootprobeContext ctx, 
        string userAugment)
    {
        var result = await Probes.Run(ctx);
        var summary = Probes.FormatSummary(result);
        var combined = CombineAugmentation(summary, userAugment);
        return (result, summary, combined);
    }

    // CombineAugmentation prepends the boot probe summary to any user-supplied
    // instructions so that both are available to the runtime.
    private static string CombineAugmentation(string summary, string user)
    {
        summary = summary.Trim();
        user = user.Trim();

        return (summary, user) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => summary + "\n\n" + user,
            ({ Length: > 0 }, _) => summary,
            (_, { Length: > 0 }) => user,
            _ => ""
        };
    }
}
