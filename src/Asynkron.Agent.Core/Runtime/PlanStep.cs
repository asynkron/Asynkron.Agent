using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// PlanStep describes an individual plan entry from OpenAI.
/// </summary>
public record PlanStep
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
    
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;
    
    [JsonPropertyName("status")]
    public PlanStatus Status { get; init; }
    
    [JsonPropertyName("waitingForId")]
    public List<string>? WaitingForId { get; init; }
    
    [JsonPropertyName("command")]
    public CommandDraft Command { get; init; } = new();
    
    [JsonPropertyName("observation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanObservation? Observation { get; init; }
    
    [JsonIgnore]
    public bool Executing { get; init; }
}
