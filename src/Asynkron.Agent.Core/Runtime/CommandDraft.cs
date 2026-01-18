using System.Text.Json.Serialization;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// CommandDraft replicates the shell command contract embedded in the plan schema.
/// </summary>
public class CommandDraft
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
    
    [JsonPropertyName("shell")]
    public string Shell { get; set; } = string.Empty;
    
    [JsonPropertyName("run")]
    public string Run { get; set; } = string.Empty;
    
    [JsonPropertyName("cwd")]
    public string Cwd { get; set; } = string.Empty;
    
    [JsonPropertyName("timeout_sec")]
    public int TimeoutSec { get; set; }
    
    [JsonPropertyName("filter_regex")]
    public string FilterRegex { get; set; } = string.Empty;
    
    [JsonPropertyName("tail_lines")]
    public int TailLines { get; set; }
    
    [JsonPropertyName("max_bytes")]
    public int MaxBytes { get; set; }
}
