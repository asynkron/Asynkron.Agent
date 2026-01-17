namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// InputEvent is the public payload that can be enqueued on the runtime input
/// queue. When Type is Prompt the Prompt field carries the actual user
/// message. Reason can be used to describe the origin of a cancel or shutdown
/// request.
/// </summary>
public class InputEvent
{
    public InputEventType Type { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
