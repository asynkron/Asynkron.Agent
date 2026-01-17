using System.Text.Json.Serialization;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// PlanResponse captures the structured assistant output.
/// </summary>
public class PlanResponse
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
    
    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Reasoning { get; set; }
    
    [JsonPropertyName("plan")]
    public List<PlanStep> Plan { get; set; } = new();
    
    [JsonPropertyName("requireHumanInput")]
    public bool RequireHumanInput { get; set; }
}
