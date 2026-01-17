namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// StatusLevel mirrors the severity levels surfaced by the TypeScript runtime.
/// It allows callers to format the output in a human friendly way while keeping
/// the Go implementation loosely coupled from presentation concerns.
/// </summary>
public enum StatusLevel
{
    /// <summary>
    /// Info is the default status level for informational events.
    /// </summary>
    Info,
    
    /// <summary>
    /// Warn signals a potential issue that did not stop execution.
    /// </summary>
    Warn,
    
    /// <summary>
    /// Error marks fatal issues that will tear down the runtime.
    /// </summary>
    Error
}
