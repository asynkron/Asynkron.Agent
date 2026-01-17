namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// ToolCall stores metadata for an assistant tool invocation.
/// </summary>
public class ToolCall
{
    public string ID { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}
