namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// InputEventType distinguishes the different kinds of inputs that can be
/// pushed into the runtime input queue.
/// </summary>
public enum InputEventType
{
    /// <summary>
    /// Prompt represents a user message that should be processed by
    /// the agent. This maps to the `prompt` input of the TypeScript runtime.
    /// </summary>
    Prompt,
    
    /// <summary>
    /// Cancel requests that the current operation is canceled.
    /// </summary>
    Cancel,
    
    /// <summary>
    /// Shutdown initiates a graceful shutdown of the runtime.
    /// </summary>
    Shutdown
}
