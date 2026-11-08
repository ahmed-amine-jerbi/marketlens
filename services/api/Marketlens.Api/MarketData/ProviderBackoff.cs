namespace Marketlens.Api.MarketData;

/// <summary>
/// Retry delays for one provider connection: doubles from <see cref="InitialDelay"/> up to
/// <see cref="MaxDelay"/>, with ±20% jitter so reconnecting workers don't hit a provider in step,
/// and never shorter than a provider's Retry-After. Not thread-safe: one instance per worker.
/// </summary>
public sealed class ProviderBackoff(Random random)
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    private const double JitterFraction = 0.2;

    private TimeSpan nextBaseDelay = InitialDelay;

    public ProviderBackoff()
        : this(Random.Shared)
    {
    }

    public TimeSpan NextDelay(TimeSpan? retryAfter = null)
    {
        var jitterFactor = 1 + ((random.NextDouble() * 2) - 1) * JitterFraction;
        var delay = nextBaseDelay * jitterFactor;
        nextBaseDelay = TimeSpan.FromTicks(Math.Min(nextBaseDelay.Ticks * 2, MaxDelay.Ticks));

        return retryAfter is { } providerMinimum && providerMinimum > delay ? providerMinimum : delay;
    }

    /// <summary>Call after a successful request or connection.</summary>
    public void Reset() => nextBaseDelay = InitialDelay;
}
