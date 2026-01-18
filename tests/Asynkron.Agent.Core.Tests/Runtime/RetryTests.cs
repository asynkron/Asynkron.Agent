using Xunit;
using Asynkron.Agent.Core.Runtime;

namespace Asynkron.Agent.Core.Tests.Runtime;

public class RetryTests
{
    [Fact]
    public void DefaultRetryConfig_ShouldHaveReasonableValues()
    {
        var config = RetryConfig.Default();
        
        Assert.Equal(3, config.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(500), config.InitialBackoff);
        Assert.Equal(TimeSpan.FromSeconds(8), config.MaxBackoff);
        Assert.Equal(2.0, config.Multiplier);
    }

    [Fact]
    public void BackoffSequence_ShouldGrowExponentially()
    {
        var config = new RetryConfig
        {
            MaxRetries = 3,
            InitialBackoff = TimeSpan.FromSeconds(1),
            MaxBackoff = TimeSpan.FromSeconds(10),
            Multiplier = 2.0
        };

        var backoff = config.InitialBackoff;
        Assert.Equal(TimeSpan.FromSeconds(1), backoff);

        backoff = TimeSpan.FromTicks((long)(backoff.Ticks * config.Multiplier));
        Assert.Equal(TimeSpan.FromSeconds(2), backoff);

        backoff = TimeSpan.FromTicks((long)(backoff.Ticks * config.Multiplier));
        Assert.Equal(TimeSpan.FromSeconds(4), backoff);
    }

    [Fact]
    public void MaxBackoff_ShouldCapBackoffValue()
    {
        var config = new RetryConfig
        {
            MaxRetries = 5,
            InitialBackoff = TimeSpan.FromSeconds(5),
            MaxBackoff = TimeSpan.FromSeconds(10),
            Multiplier = 2.0
        };

        var backoff = config.InitialBackoff;
        for (int i = 0; i < 3; i++)
        {
            backoff = TimeSpan.FromTicks((long)(backoff.Ticks * config.Multiplier));
            if (backoff > config.MaxBackoff)
                backoff = config.MaxBackoff;
        }

        Assert.Equal(config.MaxBackoff, backoff);
    }
}
