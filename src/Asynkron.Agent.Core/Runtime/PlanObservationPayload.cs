using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// PlanObservationPayload mirrors the JSON payload forwarded back to the model.
/// </summary>
public record PlanObservationPayload
{
    [JsonPropertyName("plan_observation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StepObservation>? PlanObservation { get; init; }
    
    [JsonIgnore]
    public string Stdout { get; init; } = string.Empty;
    
    [JsonIgnore]
    public string Stderr { get; init; } = string.Empty;
    
    [JsonIgnore]
    public bool Truncated { get; init; }
    
    [JsonIgnore]
    public int? ExitCode { get; init; }
    
    [JsonPropertyName("json_parse_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool JSONParseError { get; init; }
    
    [JsonPropertyName("schema_validation_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SchemaValidationError { get; init; }
    
    [JsonPropertyName("response_validation_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ResponseValidationError { get; init; }
    
    [JsonPropertyName("canceled_by_human")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CanceledByHuman { get; init; }
    
    [JsonPropertyName("operation_canceled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OperationCanceled { get; init; }
    
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Summary { get; init; } = string.Empty;
    
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Details { get; init; } = string.Empty;
}
