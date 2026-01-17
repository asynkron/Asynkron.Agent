using Microsoft.Extensions.AI;
using System;

namespace Asynkron.Agent.Core.Runtime;

/// <summary>
/// Factory for creating OpenAI clients using Microsoft.Extensions.AI.
/// This provides a modern, testable abstraction over the OpenAI API.
/// </summary>
/// <remarks>
/// Microsoft.Extensions.AI provides a unified abstraction for AI services with:
/// - Standardized IChatClient interface
/// - Built-in support for streaming
/// - Middleware pipeline (logging, caching, telemetry)
/// - Easy testing with mocks
/// - Provider-agnostic code
/// 
/// Example usage:
/// <code>
/// var chatClient = OpenAIClientFactory.CreateChatClient(apiKey, "gpt-4o");
/// var response = await chatClient.CompleteAsync(messages, cancellationToken);
/// </code>
/// </remarks>
public static class OpenAIClientFactory
{
    /// <summary>
    /// Creates an IChatClient using Microsoft.Extensions.AI for OpenAI.
    /// This is the recommended approach for new code.
    /// </summary>
    /// <param name="apiKey">OpenAI API key</param>
    /// <param name="model">Model name (e.g., "gpt-4o", "gpt-4.1")</param>
    /// <param name="baseUrl">Optional base URL override</param>
    /// <returns>IChatClient instance</returns>
    public static IChatClient CreateChatClient(string apiKey, string model, string? baseUrl = null)
    {
        var options = new OpenAI.OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            options.Endpoint = new Uri(baseUrl);
        }

        var credential = new System.ClientModel.ApiKeyCredential(apiKey);
        var openAIClient = new OpenAI.OpenAIClient(credential, options);
        return openAIClient.AsChatClient(model);
    }
}
