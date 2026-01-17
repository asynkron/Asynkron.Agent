using System.Text.Json.Serialization;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// PlanStep describes an individual plan entry from OpenAI.
/// </summary>
public class PlanStep
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = string.Empty;
    
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
    
    [JsonPropertyName("status")]
    public PlanStatus Status { get; set; }
    
    [JsonPropertyName("waitingForId")]
    public List<string> WaitingForID { get; set; } = new();
    
    [JsonPropertyName("command")]
    public CommandDraft Command { get; set; } = new();
    
    [JsonPropertyName("observation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanObservation? Observation { get; set; }
    
    [JsonIgnore]
    public bool Executing { get; set; }
}
