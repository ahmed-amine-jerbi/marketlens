using System.Diagnostics.CodeAnalysis;

using Marketlens.Api.Data;

using Microsoft.EntityFrameworkCore;

namespace Marketlens.Api.MarketData;

/// <summary>
/// No stored USDT rate is recent enough for a candle's day yet (a fresh database still loading the
/// USDT history). The backfill waits and retries rather than convert at a rate from another time.
/// </summary>
public sealed class UsdtHistoryIncompleteException(DateOnly day)
    : InvalidOperationException($"No USDT rate within {UsdtRates.MaxRateAgeDays} days before {day:yyyy-MM-dd} yet; waiting for the USDT history.")
{
    public DateOnly Day { get; } = day;
}

/// <summary>
/// Turns Binance's USDT prices into US dollars at the USDT rate, Tether's daily USD price (ADR 0007).
/// Both Binance paths go through here, so a bucket written live and by the backfill is on one
/// basis: a live tick uses the latest rate, a candle the rate of its own UTC day.
/// </summary>
public sealed class UsdtRates(QuoteBook quoteBook, AssetCatalog catalog, IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// The longest gap between two stored rates a candle may fall into. Frankfurter publishes every
    /// day of the week, so a longer gap means part of the history is still missing.
    /// </summary>
    public const int MaxRateAgeDays = 7;

    /// <summary>Converts a live tick at the latest USDT rate; false until a rate is known.</summary>
    public bool TryConvert(PriceTick usdtTick, [NotNullWhen(true)] out PriceTick? usdTick)
    {
        ArgumentNullException.ThrowIfNull(usdtTick);
        if (!quoteBook.TryGet(AssetKey.Tether, out var tether))
        {
            usdTick = null;
            return false;
        }

        usdTick = ToUsd(usdtTick, tether.PriceUsd);
        return true;
    }

    /// <summary>
    /// Converts each candle at the rate of its UTC day (see <see cref="RateOn"/>). Rates are
    /// published a day late, so today's buckets use yesterday's rate, as the live feed does.
    /// </summary>
    /// <exception cref="UsdtHistoryIncompleteException">A candle falls before the first stored rate or into a gap in them.</exception>
    public async Task<IReadOnlyList<CandleRow>> ConvertAsync(IReadOnlyList<CandleRow> usdtCandles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(usdtCandles);
        if (usdtCandles.Count == 0)
        {
            return [];
        }

        var firstDay = DayOf(usdtCandles.Min(candle => candle.BucketStartUtc));
        var rates = await LoadDailyRatesSinceAsync(firstDay.AddDays(-MaxRateAgeDays), cancellationToken);

        return usdtCandles.Select(candle =>
        {
            var day = DayOf(candle.BucketStartUtc);
            return ToUsd(candle, RateOn(rates, day) ?? throw new UsdtHistoryIncompleteException(day));
        }).ToList();
    }

    /// <summary>
    /// The latest rate on or before <paramref name="day"/>. Inside the stored history it must be at
    /// most <see cref="MaxRateAgeDays"/> older (a longer gap is missing history); after the newest
    /// rate, the newest one is used however old, as the live feed does while Frankfurter is late or
    /// down. Null before the first rate. <paramref name="ratesByDay"/> is in date order.
    /// </summary>
    public static decimal? RateOn(IReadOnlyList<DailyUsdPrice> ratesByDay, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(ratesByDay);

        // Binary search for the last rate dated on or before the day: five years of daily candles
        // against five years of rates stays O(n log n).
        var latestIndex = -1;
        var low = 0;
        var high = ratesByDay.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (ratesByDay[middle].Date <= day)
            {
                latestIndex = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (latestIndex < 0)
        {
            return null;
        }

        var isAfterNewestRate = latestIndex == ratesByDay.Count - 1;
        var isRecentEnough = ratesByDay[latestIndex].Date >= day.AddDays(-MaxRateAgeDays);
        return isAfterNewestRate || isRecentEnough ? ratesByDay[latestIndex].PriceUsd : null;
    }

    public static PriceTick ToUsd(PriceTick usdtTick, decimal usdPerUsdt)
    {
        ArgumentNullException.ThrowIfNull(usdtTick);
        return usdtTick with
        {
            PriceUsd = Multiply(usdtTick.PriceUsd, usdPerUsdt),
            ReferencePriceUsd = usdtTick.ReferencePriceUsd is { } reference ? Multiply(reference, usdPerUsdt) : null,
        };
    }

    /// <summary>Converts the four prices; the volume is counted in the coin itself and stays as it is.</summary>
    public static CandleRow ToUsd(CandleRow usdtCandle, decimal usdPerUsdt)
    {
        ArgumentNullException.ThrowIfNull(usdtCandle);
        return usdtCandle with
        {
            OpenUsd = Multiply(usdtCandle.OpenUsd, usdPerUsdt),
            HighUsd = Multiply(usdtCandle.HighUsd, usdPerUsdt),
            LowUsd = Multiply(usdtCandle.LowUsd, usdPerUsdt),
            CloseUsd = Multiply(usdtCandle.CloseUsd, usdPerUsdt),
        };
    }

    // Prices are stored as numeric(38,18); rounding here keeps live and backfilled values identical.
    private static decimal Multiply(decimal usdtPrice, decimal usdPerUsdt) =>
        decimal.Round(usdtPrice * usdPerUsdt, 18, MidpointRounding.ToEven);

    private static DateOnly DayOf(DateTime utc) => DateOnly.FromDateTime(utc);

    /// <summary>Every stored rate from <paramref name="fromDay"/> on, so the newest one is the newest there is.</summary>
    private async Task<IReadOnlyList<DailyUsdPrice>> LoadDailyRatesSinceAsync(DateOnly fromDay, CancellationToken cancellationToken)
    {
        if (!catalog.TryGet(AssetKey.Tether, out var tether))
        {
            return [];
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var fromUtc = fromDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var closes = await db.PriceCandles
            .AsNoTracking()
            .Where(candle => candle.AssetId == tether.Id && candle.Interval == CandleInterval.OneDay && candle.BucketStartUtc >= fromUtc)
            .OrderBy(candle => candle.BucketStartUtc)
            .Select(candle => new { candle.BucketStartUtc, candle.CloseUsd })
            .ToListAsync(cancellationToken);

        return closes.Select(close => new DailyUsdPrice(AssetKey.Tether, DayOf(close.BucketStartUtc), close.CloseUsd)).ToList();
    }
}
