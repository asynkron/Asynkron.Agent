using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// PlanObservationPayload mirrors the JSON payload forwarded back to the model.
/// </summary>
public class PlanObservationPayload
{
    [JsonPropertyName("plan_observation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StepObservation>? PlanObservation { get; set; }
    
    [JsonIgnore]
    public string Stdout { get; set; } = string.Empty;
    
    [JsonIgnore]
    public string Stderr { get; set; } = string.Empty;
    
    [JsonIgnore]
    public bool Truncated { get; set; }
    
    [JsonIgnore]
    public int? ExitCode { get; set; }
    
    [JsonPropertyName("json_parse_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool JSONParseError { get; set; }
    
    [JsonPropertyName("schema_validation_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SchemaValidationError { get; set; }
    
    [JsonPropertyName("response_validation_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ResponseValidationError { get; set; }
    
    [JsonPropertyName("canceled_by_human")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CanceledByHuman { get; set; }
    
    [JsonPropertyName("operation_canceled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool OperationCanceled { get; set; }
    
    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Summary { get; set; } = string.Empty;
    
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Details { get; set; } = string.Empty;
}
