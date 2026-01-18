using System.Text.Json.Serialization;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// PlanObservation bundles the payload with optional metadata.
/// </summary>
public record PlanObservation
{
    [JsonPropertyName("observation_for_llm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PlanObservationPayload? ObservationForLlm { get; init; }
}
