using System.Text.Json.Serialization;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// ChatMessage stores a single message exchanged with OpenAI.
/// </summary>
public class ChatMessage
{
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? ToolCallID { get; set; }
    public string? Name { get; set; }
    public DateTime Timestamp { get; set; }
    public List<ToolCall> ToolCalls { get; set; } = new();
    public int Pass { get; set; }
    
    /// <summary>
    /// Summarized marks messages that were synthesized by the compactor so we
    /// avoid repeatedly summarizing the same entry.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Summarized { get; set; }
}
