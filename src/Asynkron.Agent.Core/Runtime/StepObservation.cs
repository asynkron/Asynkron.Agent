using System.Text.Json.Serialization;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// StepObservation summarizes the outcome for a specific plan step.
/// </summary>
public class StepObservation
{
    [JsonPropertyName("id")]
    public string ID { get; set; } = string.Empty;
    
    [JsonPropertyName("status")]
    public PlanStatus Status { get; set; }
    
    [JsonPropertyName("stdout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Stdout { get; set; } = string.Empty;
    
    [JsonPropertyName("stderr")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Stderr { get; set; } = string.Empty;
    
    [JsonPropertyName("exit_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; set; }
    
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Details { get; set; } = string.Empty;
    
    [JsonPropertyName("truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; set; }
}
