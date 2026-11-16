using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Options;

namespace Marketlens.Api.MarketData;

public sealed class QuoteBookOptions
{
    public const string Section = "MarketData:QuoteBook";

    /// <summary>
    /// How long an unchanged price (same price and reference) stays unannounced. Feeds re-report the
    /// same price every second; one announcement per interval keeps "updated N seconds ago" honest
    /// without fanning every repeat out to clients and candles. Zero announces every tick.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
}

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
public sealed class QuoteBook(TimeProvider timeProvider, IOptions<QuoteBookOptions> options)
{
    private const string PivotSource = "pivot";

    private readonly ConcurrentDictionary<AssetKey, UsdQuote> latestQuotes = new();
    private readonly ConcurrentDictionary<AssetKey, DateTime> lastAnnouncedUtc = new();

    /// <summary>
    /// Raised after an accepted tick that moved the price or its reference, and at most once per
    /// <see cref="QuoteBookOptions.HeartbeatInterval"/> for an unchanged one, on the ingesting thread.
    /// Handlers must be quick and must not throw: they run inside a provider's receive loop.
    /// </summary>
    public event Action<UsdQuote>? QuoteUpdated;

    /// <summary>
    /// Records a tick if it is newer than what the book holds. Returns false for an older or
    /// out-of-order tick, which leaves the book unchanged. A tick without a reference price (a single
    /// trade) keeps the reference the book already has. An unchanged price is stored with its newer
    /// time either way; whether it is announced follows <see cref="QuoteUpdated"/>.
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
                    Announce(incoming);
                    return true;
                }

                continue;
            }

            if (incoming.AsOfUtc <= current.AsOfUtc)
            {
                return false;
            }

            var merged = incoming with { ReferencePriceUsd = incoming.ReferencePriceUsd ?? current.ReferencePriceUsd };
            if (!latestQuotes.TryUpdate(tick.Asset, merged, current))
            {
                continue;
            }

            if (!IsRepeat(merged, current))
            {
                Announce(merged);
            }

            return true;
        }
    }

    // A repeat reports the same price and reference again; Binance's 24-hour open rolls while the
    // last price stands still, which changes the change percentage, so it is not a repeat.
    private bool IsRepeat(UsdQuote merged, UsdQuote current) =>
        merged.PriceUsd == current.PriceUsd
        && merged.ReferencePriceUsd == current.ReferencePriceUsd
        && lastAnnouncedUtc.TryGetValue(merged.Asset, out var announcedUtc)
        && merged.AsOfUtc - announcedUtc < options.Value.HeartbeatInterval;

    private void Announce(UsdQuote quote)
    {
        lastAnnouncedUtc[quote.Asset] = quote.AsOfUtc;
        QuoteUpdated?.Invoke(quote);
    }

    /// <summary>
    /// Sets the price an asset's change is measured against (a new previous close), whatever the age
    /// of the quote that reported it. Ignored for an asset without a price yet.
    /// </summary>
    public void SetReferencePrice(AssetKey asset, decimal referencePriceUsd)
    {
        if (referencePriceUsd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(referencePriceUsd), referencePriceUsd, $"Reference price for {asset} must be positive.");
        }

        while (latestQuotes.TryGetValue(asset, out var current))
        {
            if (current.ReferencePriceUsd == referencePriceUsd)
            {
                return;
            }

            if (latestQuotes.TryUpdate(asset, current with { ReferencePriceUsd = referencePriceUsd }, current))
            {
                // The next tick carries the new change percentage, so it must not count as a repeat.
                lastAnnouncedUtc.TryRemove(asset, out _);
                return;
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

    /// <summary>
    /// Drops every quote one source reported, silently: sample prices once real data takes over.
    /// They carry the time they were made up, so left in place they would outrank an older real
    /// price (a last trade before the close) until the next trade. Returns how many were dropped.
    /// </summary>
    public int RemoveBySource(string source)
    {
        var removedCount = 0;
        foreach (var quote in latestQuotes.Values.Where(quote => quote.Source == source))
        {
            if (latestQuotes.TryRemove(new KeyValuePair<AssetKey, UsdQuote>(quote.Asset, quote)))
            {
                removedCount++;
            }
        }

        return removedCount;
    }

    /// <summary>
    /// Puts saved quotes back after a restart without raising <see cref="QuoteUpdated"/>: they are
    /// history, not news. A quote the book already holds a newer price for is skipped.
    /// </summary>
    public void Restore(IEnumerable<UsdQuote> savedQuotes)
    {
        ArgumentNullException.ThrowIfNull(savedQuotes);
        foreach (var saved in savedQuotes.Where(saved => saved.Asset != AssetKey.UsDollar && saved.PriceUsd > 0))
        {
            latestQuotes.AddOrUpdate(saved.Asset, saved, (_, current) => current.AsOfUtc >= saved.AsOfUtc ? current : saved);
        }
    }
}
