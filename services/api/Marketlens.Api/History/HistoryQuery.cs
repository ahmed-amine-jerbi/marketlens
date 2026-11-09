using Marketlens.Api.Conversions;
using Marketlens.Api.Data;
using Marketlens.Api.MarketData;

using Microsoft.EntityFrameworkCore;

namespace Marketlens.Api.History;

public enum ChartRange
{
    OneDay,
    OneWeek,
    OneMonth,
    ThreeMonths,
    OneYear,
    FiveYears,
}

public sealed record HistoryPoint(DateTime T, decimal Close);

public sealed record HistoryResponse(string Interval, AssetKey In, IReadOnlyList<HistoryPoint> Points);

/// <summary>
/// Reads chart history for an asset in a display currency (D36). Each range picks the finest stored
/// candle interval that covers it, every point is converted with the display currency's price at that
/// moment, and the series is down-sampled to at most <see cref="MaxPoints"/> points.
/// </summary>
public sealed class HistoryQuery(IServiceScopeFactory scopeFactory, AssetCatalog catalog, TimeProvider timeProvider)
{
    public const int MaxPoints = 500;

    private const int OneDayStepMinutes = 5;

    public static bool TryParseRange(string? text, out ChartRange range)
    {
        (var isKnown, range) = text switch
        {
            "1D" => (true, ChartRange.OneDay),
            "1W" => (true, ChartRange.OneWeek),
            "1M" => (true, ChartRange.OneMonth),
            "3M" => (true, ChartRange.ThreeMonths),
            "1Y" => (true, ChartRange.OneYear),
            "5Y" => (true, ChartRange.FiveYears),
            _ => (false, default),
        };
        return isKnown;
    }

    /// <summary>The span a range shows and the candle interval it reads (plan, data flows).</summary>
    public static (TimeSpan Span, CandleInterval Interval) Plan(ChartRange range) => range switch
    {
        ChartRange.OneDay => (TimeSpan.FromDays(1), CandleInterval.OneMinute),
        ChartRange.OneWeek => (TimeSpan.FromDays(7), CandleInterval.OneHour),
        ChartRange.OneMonth => (TimeSpan.FromDays(30), CandleInterval.OneHour),
        ChartRange.ThreeMonths => (TimeSpan.FromDays(91), CandleInterval.OneDay),
        ChartRange.OneYear => (TimeSpan.FromDays(365), CandleInterval.OneDay),
        ChartRange.FiveYears => (TimeSpan.FromDays(5 * 365), CandleInterval.OneDay),
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
    };

    /// <summary>
    /// Keeps the last close of each group of consecutive points so at most <paramref name="maxPoints"/>
    /// remain; the newest point always survives.
    /// </summary>
    public static IReadOnlyList<HistoryPoint> DownSample(IReadOnlyList<HistoryPoint> points, int maxPoints)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count <= maxPoints)
        {
            return points;
        }

        var groupSize = (int)Math.Ceiling(points.Count / (double)maxPoints);
        return points.Reverse().Chunk(groupSize).Select(group => group[0]).Reverse().ToList();
    }

    public async Task<HistoryResponse> ReadAsync(AssetKey asset, AssetKey displayIn, ChartRange range, CancellationToken cancellationToken)
    {
        var (span, plannedInterval) = Plan(range);

        // Fiat and Tether have daily reference rates only (D28), so an intraday range of them shows
        // daily points, whatever they are shown in.
        var isDailyOnly = asset.HasDailyPricesOnly;
        var interval = isDailyOnly ? CandleInterval.OneDay : plannedInterval;
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var fromUtc = nowUtc - (isDailyOnly && span < TimeSpan.FromDays(7) ? TimeSpan.FromDays(7) : span);

        var assetCloses = await ReadClosesAsync(asset, interval, fromUtc, cancellationToken);
        var displayCloses = displayIn == AssetKey.UsDollar
            ? null
            : await ReadDisplayClosesAsync(displayIn, interval, fromUtc, cancellationToken);

        var points = ToDisplayCurrency(assetCloses, displayCloses);
        if (range == ChartRange.OneDay && interval == CandleInterval.OneMinute)
        {
            points = EveryNthMinute(points, OneDayStepMinutes);
        }

        return new HistoryResponse(ColumnName(interval), displayIn, DownSample(points, MaxPoints));
    }

    private async Task<IReadOnlyList<(DateTime BucketUtc, decimal CloseUsd)>> ReadClosesAsync(AssetKey asset, CandleInterval interval, DateTime fromUtc, CancellationToken cancellationToken)
    {
        if (!catalog.TryGet(asset, out var entry))
        {
            return [];
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var closes = await db.PriceCandles
            .AsNoTracking()
            .Where(candle => candle.AssetId == entry.Id && candle.Interval == interval && candle.BucketStartUtc >= fromUtc)
            .OrderBy(candle => candle.BucketStartUtc)
            .Select(candle => new { candle.BucketStartUtc, candle.CloseUsd })
            .ToListAsync(cancellationToken);

        return closes.Select(close => (DateTime.SpecifyKind(close.BucketStartUtc, DateTimeKind.Utc), close.CloseUsd)).ToList();
    }

    /// <summary>
    /// The display currency's closes at the finest interval it has: fiat (and Tether) only have days,
    /// so an hourly chart in euros uses each day's euro rate. A few extra days before the window
    /// cover weekends.
    /// </summary>
    private Task<IReadOnlyList<(DateTime BucketUtc, decimal CloseUsd)>> ReadDisplayClosesAsync(AssetKey displayIn, CandleInterval interval, DateTime fromUtc, CancellationToken cancellationToken)
    {
        var displayInterval = displayIn.HasDailyPricesOnly ? CandleInterval.OneDay : interval;
        return ReadClosesAsync(displayIn, displayInterval, fromUtc.AddDays(-7), cancellationToken);
    }

    /// <summary>Converts each close with the latest display-currency close at or before it; points before any display price are dropped.</summary>
    private static List<HistoryPoint> ToDisplayCurrency(
        IReadOnlyList<(DateTime BucketUtc, decimal CloseUsd)> assetCloses,
        IReadOnlyList<(DateTime BucketUtc, decimal CloseUsd)>? displayCloses)
    {
        if (displayCloses is null)
        {
            return assetCloses.Select(close => new HistoryPoint(close.BucketUtc, ConversionCalculator.RoundToSignificantDigits(close.CloseUsd))).ToList();
        }

        var points = new List<HistoryPoint>(assetCloses.Count);
        var displayIndex = -1;
        foreach (var (bucketUtc, closeUsd) in assetCloses)
        {
            while (displayIndex + 1 < displayCloses.Count && displayCloses[displayIndex + 1].BucketUtc <= bucketUtc)
            {
                displayIndex++;
            }

            if (displayIndex >= 0)
            {
                points.Add(new HistoryPoint(bucketUtc, ConversionCalculator.Rate(closeUsd, displayCloses[displayIndex].CloseUsd)));
            }
        }

        return points;
    }

    private static List<HistoryPoint> EveryNthMinute(List<HistoryPoint> points, int stepMinutes) =>
        points.Where((point, index) => point.T.Minute % stepMinutes == 0 || index == points.Count - 1).ToList();

    private static string ColumnName(CandleInterval interval) => PriceCandleConfiguration.ToColumnValue(interval);
}
