using Marketlens.Api.MarketData;

namespace Marketlens.Api.Tests.MarketData;

public sealed class ProviderBackoffTests
{
    /// <summary>A random source that always returns the middle of the range, so jitter is zero.</summary>
    private sealed class NoJitter : Random
    {
        public override double NextDouble() => 0.5;
    }

    [Fact]
    public void NextDelay_doubles_from_one_second_up_to_one_minute()
    {
        var backoff = new ProviderBackoff(new NoJitter());

        var delays = Enumerable.Range(0, 8).Select(_ => backoff.NextDelay().TotalSeconds).ToList();

        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60], delays);
    }

    [Fact]
    public void Reset_starts_again_from_the_initial_delay()
    {
        var backoff = new ProviderBackoff(new NoJitter());
        backoff.NextDelay();
        backoff.NextDelay();

        backoff.Reset();

        Assert.Equal(ProviderBackoff.InitialDelay, backoff.NextDelay());
    }

    [Fact]
    public void NextDelay_never_waits_less_than_the_providers_retry_after()
    {
        var backoff = new ProviderBackoff(new NoJitter());

        var delay = backoff.NextDelay(retryAfter: TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void NextDelay_jitter_stays_within_twenty_percent()
    {
        var backoff = new ProviderBackoff(new Random(42));

        var delays = Enumerable.Range(0, 200).Select(_ =>
        {
            backoff.Reset();
            return backoff.NextDelay().TotalSeconds;
        }).ToList();

        Assert.All(delays, seconds => Assert.InRange(seconds, 0.8, 1.2));
        Assert.True(delays.Distinct().Count() > 1);
    }
}
