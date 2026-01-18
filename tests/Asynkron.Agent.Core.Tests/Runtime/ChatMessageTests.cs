using Xunit;
using Asynkron.Agent.Core.Runtime;
using System;
using System.Collections.Generic;

namespace Asynkron.Agent.Core.Tests.Runtime;

public class ChatMessageTests
{
    [Fact]
    public void ChatMessage_DefaultValues_ShouldBeSet()
    {
        var message = new ChatMessage();
        
        Assert.Equal(MessageRole.System, message.Role);
        Assert.Empty(message.Content);
        Assert.Empty(message.ToolCalls);
        Assert.False(message.Summarized);
    }

    [Fact]
    public void ChatMessage_WithToolCalls_ShouldStoreCorrectly()
    {
        var message = new ChatMessage
        {
            Role = MessageRole.Assistant,
            Content = "Let me help you",
            ToolCalls = new List<ToolCall>
            {
                new() { ID = "call1", Name = "get_weather", Arguments = "{\"city\":\"NYC\"}" }
            }
        };
        
        Assert.Equal(MessageRole.Assistant, message.Role);
        Assert.Single(message.ToolCalls);
        Assert.Equal("call1", message.ToolCalls[0].ID);
    }

    [Fact]
    public void MessageRole_AllValues_ShouldBeAccessible()
    {
        Assert.NotEqual(MessageRole.System, MessageRole.User);
        Assert.NotEqual(MessageRole.User, MessageRole.Assistant);
        Assert.NotEqual(MessageRole.Assistant, MessageRole.Tool);
    }
}
