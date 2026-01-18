using System.Text.Json.Serialization;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// RuntimeEvent is the cross-language payload for messages flowing out of the
/// runtime. The structure stays intentionally small to keep it easy to consume
/// from CLIs, HTTP handlers or tests.
/// </summary>
public class RuntimeEvent
{
    [JsonPropertyName("type")]
    public EventType Type { get; set; }
    
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
    
    [JsonPropertyName("level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public StatusLevel Level { get; set; }
    
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Metadata { get; set; }
    
    [JsonPropertyName("pass")]
    public int Pass { get; set; }
    
    [JsonPropertyName("agent")]
    public string Agent { get; set; } = string.Empty;
}
