using Xunit;
using Asynkron.Agent.Core.Runtime;
using System;

namespace Asynkron.Agent.Core.Tests.Runtime;

public sealed class OpenAIClientFactoryTests
{
    [Fact]
    public void CreateChatClient_WithValidApiKey_ShouldNotThrow()
    {
        // Arrange
        var apiKey = "test-key-123";
        var model = "gpt-4o";

        // Act & Assert
        var exception = Record.Exception(() => 
            OpenAIClientFactory.CreateChatClient(apiKey, model));
        
        Assert.Null(exception);
    }

    [Fact]
    public void CreateChatClient_WithBaseUrl_ShouldNotThrow()
    {
        // Arrange
        var apiKey = "test-key-123";
        var model = "gpt-4o";
        var baseUrl = "https://custom.openai.com/v1";

        // Act & Assert
        var exception = Record.Exception(() => 
            OpenAIClientFactory.CreateChatClient(apiKey, model, baseUrl));
        
        Assert.Null(exception);
    }

    [Fact]
    public void CreateChatClient_ShouldReturnIChatClient()
    {
        // Arrange
        var apiKey = "test-key-123";
        var model = "gpt-4o";

        // Act
        var client = OpenAIClientFactory.CreateChatClient(apiKey, model);

        // Assert
        Assert.NotNull(client);
        Assert.IsAssignableFrom<Microsoft.Extensions.AI.IChatClient>(client);
    }
}
