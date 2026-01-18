using Xunit;
using Asynkron.Agent.Core.Runtime;

namespace Asynkron.Agent.Core.Tests.Runtime;

public sealed class RuntimeOptionsTests
{
    [Fact]
    public void DefaultModel_ShouldBe_Gpt41()
    {
        var options = new RuntimeOptions();
        Assert.Equal("gpt-4.1", options.Model);
    }

    [Fact]
    public void WithDefaults_ShouldSetReasonableDefaults()
    {
        var options = new RuntimeOptions
        {
            ApiKey = "test-key"
        };

        var withDefaults = options.WithDefaults();

        Assert.NotNull(withDefaults);
        Assert.Equal("test-key", withDefaults.ApiKey);
        Assert.Equal(128000, withDefaults.MaxContextTokens);
        Assert.Equal(0.85, withDefaults.CompactWhenPercent);
        Assert.Equal(4, withDefaults.InputBuffer);
        Assert.Equal(16, withDefaults.OutputBuffer);
        Assert.True(withDefaults.UseStreaming);
    }

    [Fact]
    public void WithExpression_ShouldCreateNewInstance()
    {
        var original = new RuntimeOptions { ApiKey = "key1", Model = "gpt-4" };
        var modified = original with { Model = "gpt-4o" };

        Assert.Equal("key1", original.ApiKey);
        Assert.Equal("gpt-4", original.Model);
        Assert.Equal("key1", modified.ApiKey);
        Assert.Equal("gpt-4o", modified.Model);
    }

    [Fact]
    public void ExitCommands_ShouldHaveDefaultValues()
    {
        var options = new RuntimeOptions();
        Assert.Contains("exit", options.ExitCommands);
        Assert.Contains("quit", options.ExitCommands);
    }

    [Theory]
    [InlineData("gpt-4.1", 128000, 0.85)]
    [InlineData("o1", 128000, 0.8)]
    public void ModelSpecificDefaults_ShouldBeApplied(string model, int expectedTokens, double expectedPercent)
    {
        var options = new RuntimeOptions
        {
            ApiKey = "test-key",
            Model = model,
            MaxContextTokens = 0,  // Force it to use model-specific defaults
            CompactWhenPercent = 0  // Force it to use model-specific defaults
        }.WithDefaults();

        Assert.Equal(expectedTokens, options.MaxContextTokens);
        Assert.Equal(expectedPercent, options.CompactWhenPercent);
    }
}
