using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Marketlens.Api.MarketData;

/// <summary>The latest known USD price of an asset.</summary>
public sealed record UsdQuote(
    AssetKey Asset,
    decimal PriceUsd,
    decimal? ReferencePriceUsd,
    DateTime AsOfUtc,
    string Source,
    Freshness Freshness);

/// <summary>
/// The latest USD price of every asset, in memory. Ingestion writes ticks; quotes, conversions and
/// alerts read from here, never from providers. USD is the pivot and always prices at exactly 1.
/// </summary>
public sealed class QuoteBook(TimeProvider timeProvider)
{
    private const string PivotSource = "pivot";

    private readonly ConcurrentDictionary<AssetKey, UsdQuote> latestQuotes = new();

    /// <summary>
    /// Records a tick if it is newer than what the book holds. Returns false for an older or
    /// out-of-order tick, which leaves the book unchanged.
    /// </summary>
    public bool Apply(PriceTick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        if (tick.PriceUsd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick.PriceUsd, $"Price for {tick.Asset} from {tick.Source} must be positive.");
        }

        if (tick.Asset == AssetKey.UsDollar)
        {
            return false;
        }

        var incoming = new UsdQuote(tick.Asset, tick.PriceUsd, tick.ReferencePriceUsd, tick.ObservedAtUtc, tick.Source, tick.Freshness);
        while (true)
        {
            if (!latestQuotes.TryGetValue(tick.Asset, out var current))
            {
                if (latestQuotes.TryAdd(tick.Asset, incoming))
                {
                    return true;
                }

                continue;
            }

            if (incoming.AsOfUtc <= current.AsOfUtc)
            {
                return false;
            }

            if (latestQuotes.TryUpdate(tick.Asset, incoming, current))
            {
                return true;
            }
        }
    }

    public bool TryGet(AssetKey asset, [MaybeNullWhen(false)] out UsdQuote quote)
    {
        if (asset == AssetKey.UsDollar)
        {
            quote = new UsdQuote(AssetKey.UsDollar, 1m, 1m, timeProvider.GetUtcNow().UtcDateTime, PivotSource, Freshness.Live);
            return true;
        }

        return latestQuotes.TryGetValue(asset, out quote);
    }

    public IReadOnlyCollection<UsdQuote> Snapshot() => latestQuotes.Values.ToList();
}
