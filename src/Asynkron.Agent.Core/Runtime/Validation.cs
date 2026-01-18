using System.Text;
using System.Text.Json;
using Asynkron.Agent.Core.Schema;
using Json.Schema;

namespace Asynkron.Agent.Core.Runtime;

public sealed partial class Runtime
{
    private const int ValidationDetailLimit = 512;
    private static readonly TimeSpan ValidationBackoffBase = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ValidationBackoffMax = TimeSpan.FromSeconds(4);
    private const int ValidationBackoffMaxExp = 5;
    
    private static JsonSchema? _planSchemaLoader;
    private static Exception? _planSchemaLoaderErr;
    private static readonly SemaphoreSlim _planSchemaLoaderOnce = new(1, 1);
    
    private sealed class SchemaValidationError(List<string> issues) : Exception(issues.Count == 0
        ? "plan response failed schema validation"
        : string.Join("; ", issues))
    {
        public List<string> Issues { get; } = issues;
    }
    
    // validatePlanToolCall ensures the assistant response is valid JSON and
    // satisfies the plan schema before we hydrate a PlanResponse structure.
    // Returning retry=true signals that the helper produced feedback for the
    // assistant and the runtime should request a new plan immediately.
    private async Task<(PlanResponse? plan, bool retry, Exception? error)> ValidatePlanToolCall(
        ToolCall toolCall, 
        CancellationToken cancellationToken)
    {
        var trimmedArgs = toolCall.Arguments.Trim();
        if (string.IsNullOrEmpty(trimmedArgs))
        {
            var payload = new PlanObservationPayload
            {
                JSONParseError = true,
                ResponseValidationError = true,
                Summary = "Assistant called the tool without providing arguments.",
                Details = "tool arguments were empty"
            };
            HandlePlanValidationFailure(toolCall, payload, BuildValidationAutoPrompt(payload));
            return (null, true, null);
        }
        
        PlanResponse? plan;
        try
        {
            plan = JsonSerializer.Deserialize<PlanResponse>(toolCall.Arguments);
        }
        catch (JsonException err)
        {
            var payload = new PlanObservationPayload
            {
                JSONParseError = true,
                ResponseValidationError = true,
                Summary = "Tool call arguments were not valid JSON.",
                Details = err.Message
            };
            HandlePlanValidationFailure(toolCall, payload, BuildValidationAutoPrompt(payload));
            return (null, true, null);
        }
        
        var schemaErr = await ValidatePlanAgainstSchema(toolCall.Arguments);
        if (schemaErr != null)
        {
            if (schemaErr is SchemaValidationError schemaValidationErr)
            {
                var payload = new PlanObservationPayload
                {
                    SchemaValidationError = true,
                    ResponseValidationError = true,
                    Summary = "Tool call arguments failed schema validation.",
                    Details = schemaValidationErr.Message
                };
                HandlePlanValidationFailure(toolCall, payload, BuildValidationAutoPrompt(payload));
                return (null, true, null);
            }
            // Non-schema validation error (e.g., schema loading error)
            return (null, false, new Exception($"validatePlanToolCall: schema validation error: {schemaErr.Message}", schemaErr));
        }
        
        return (plan, false, null);
    }
    
    private static async Task<Exception?> ValidatePlanAgainstSchema(string raw)
    {
        var loader = await LoadPlanSchema();
        if (loader.error != null)
        {
            return new Exception($"runtime: load plan schema: {loader.error.Message}", loader.error);
        }
        
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(raw);
            var result = loader.schema!.Evaluate(doc.RootElement);
            
            if (result.IsValid)
            {
                return null;
            }
            
            var issues = new List<string>();
            CollectErrors(result, issues);
            
            return new SchemaValidationError(issues);
        }
        catch (Exception err)
        {
            return new Exception($"runtime: schema validation error: {err.Message}", err);
        }
        finally
        {
            doc?.Dispose();
        }
    }
    
    private static void CollectErrors(EvaluationResults result, List<string> issues)
    {
        if (result is { IsValid: false, Errors: not null })
        {
            foreach (var (key, value) in result.Errors)
            {
                issues.Add($"{key}: {value}");
            }
        }
        
        foreach (var detail in result.Details)
        {
            CollectErrors(detail, issues);
        }
    }
    
    private static async Task<(JsonSchema? schema, Exception? error)> LoadPlanSchema()
    {
        await _planSchemaLoaderOnce.WaitAsync();
        try
        {
            if (_planSchemaLoader == null && _planSchemaLoaderErr == null)
            {
                try
                {
                    var schemaDict = PlanSchema.GetPlanResponseSchema();
                    var schemaJson = JsonSerializer.Serialize(schemaDict);
                    _planSchemaLoader = JsonSchema.FromText(schemaJson);
                }
                catch (Exception err)
                {
                    _planSchemaLoaderErr = err;
                }
            }
            
            return (_planSchemaLoader, _planSchemaLoaderErr);
        }
        finally
        {
            _planSchemaLoaderOnce.Release();
        }
    }
    
    private void HandlePlanValidationFailure(ToolCall toolCall, PlanObservationPayload payload, string autoPrompt)
    {
        payload.Details = payload.Details.Trim();
        
        var metadata = new Dictionary<string, object>
        {
            ["details"] = payload.Details
        };
        if (!string.IsNullOrEmpty(toolCall.ID))
        {
            metadata["tool_call_id"] = toolCall.ID;
        }
        if (!string.IsNullOrEmpty(toolCall.Name))
        {
            metadata["tool_name"] = toolCall.Name;
        }
        
        var message = payload.Summary;
        var details = payload.Details.Trim();
        if (!string.IsNullOrEmpty(details))
        {
            message = $"{message} Details: {details}";
        }
        
        Emit(new RuntimeEvent
        {
            Type = EventType.Status,
            Message = message,
            Level = StatusLevel.Warn,
            Metadata = metadata
        });
        
        AppendHistory(new ChatMessage
        {
            Role = MessageRole.Assistant,
            Timestamp = DateTime.Now,
            ToolCalls = [toolCall]
        });
        
        if (!string.IsNullOrEmpty(toolCall.ID))
        {
            var (toolMessage, err) = CommandExecutor.BuildToolMessage(payload);
            if (err != null)
            {
                Emit(new RuntimeEvent
                {
                    Type = EventType.Error,
                    Message = $"Failed to encode validation feedback: {err.Message}",
                    Level = StatusLevel.Error
                });
            }
            else
            {
                AppendHistory(new ChatMessage
                {
                    Role = MessageRole.Tool,
                    Content = toolMessage,
                    ToolCallID = toolCall.ID,
                    Name = toolCall.Name,
                    Timestamp = DateTime.Now
                });
            }
        }
        
        if (!string.IsNullOrWhiteSpace(autoPrompt))
        {
            AppendHistory(new ChatMessage
            {
                Role = MessageRole.User,
                Content = autoPrompt,
                Timestamp = DateTime.Now
            });
        }
    }
    
    private static string BuildValidationAutoPrompt(PlanObservationPayload payload)
    {
        var summary = payload.Summary.Trim();
        if (string.IsNullOrEmpty(summary))
        {
            summary = "The previous tool call response could not be processed.";
        }
        var details = TruncateForPrompt(payload.Details.Trim(), ValidationDetailLimit);
        
        var builder = new StringBuilder();
        builder.Append(summary);
        if (!string.IsNullOrEmpty(details))
        {
            builder.Append(" Details: ");
            builder.Append(details);
        }
        builder.Append(" Please call ");
        builder.Append(PlanSchema.ToolName);
        builder.Append(" again with JSON that strictly matches the provided schema.");
        return builder.ToString();
    }
    
    private static string TruncateForPrompt(string value, int limit)
    {
        if (limit <= 0)
        {
            return value;
        }
        if (value.Length <= limit)
        {
            return value;
        }
        return value[..limit] + "…";
    }
    
    private static TimeSpan ComputeValidationBackoff(int attempt)
    {
        if (attempt <= 0)
        {
            attempt = 1;
        }
        var exp = attempt - 1;
        if (exp > ValidationBackoffMaxExp)
        {
            exp = ValidationBackoffMaxExp;
        }
        
        var multiplier = 1 << exp;
        var delay = ValidationBackoffBase * multiplier;
        if (delay > ValidationBackoffMax)
        {
            return ValidationBackoffMax;
        }
        if (delay < ValidationBackoffBase)
        {
            return ValidationBackoffBase;
        }
        return delay;
    }
}
