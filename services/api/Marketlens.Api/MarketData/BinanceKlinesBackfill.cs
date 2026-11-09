using Marketlens.Api.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marketlens.Api.MarketData;

/// <summary>
/// Fills crypto candle history from Binance klines: everything inside each interval's retention
/// window when the stored candles don't reach back to its start (a fresh database, or live buckets
/// written before the history); otherwise only the gap since the newest stored bucket (downtime, a
/// dropped stream). Klines are in USDT and are stored in USD at each day's USDT rate; until that
/// history is loaded the run waits and retries. Keyless, so crypto charts and forecasts work with
/// no API keys (D28).
/// </summary>
public sealed partial class BinanceKlinesBackfill(
    BinanceMarketDataClient binance,
    AssetCatalog catalog,
    CandleWriter candleWriter,
    UsdtRates usdtRates,
    IServiceScopeFactory scopeFactory,
    IOptions<BinanceOptions> options,
    TimeProvider timeProvider,
    ILogger<BinanceKlinesBackfill> logger) : BackgroundService
{
    private static readonly CandleInterval[] Intervals = [CandleInterval.OneDay, CandleInterval.OneHour, CandleInterval.OneMinute];

    /// <summary>Intervals of slack before the oldest stored bucket counts as a missing past.</summary>
    private const int OldestBucketToleranceIntervals = 2;

    private readonly ProviderBackoff backoff = new();

    /// <summary>
    /// Where a fill starts: the newest stored bucket when the stored candles reach back to the
    /// window start, else the window start itself (upserts make the refetch of stored buckets
    /// harmless). A coin listed inside the window refetches its listing-to-now span each run.
    /// </summary>
    public static DateTime FillStartUtc((DateTime OldestUtc, DateTime NewestUtc)? storedSpan, DateTime windowStartUtc, TimeSpan intervalLength)
    {
        if (storedSpan is not var (oldestUtc, newestUtc))
        {
            return windowStartUtc;
        }

        var reachesWindowStart = oldestUtc <= windowStartUtc + (intervalLength * OldestBucketToleranceIntervals);
        return reachesWindowStart && newestUtc > windowStartUtc ? newestUtc : windowStartUtc;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                if (await FillGapsAsync(stoppingToken) is { } waitingForDay)
                {
                    delay = backoff.NextDelay();
                    LogWaitingForUsdtHistory(logger, waitingForDay, delay);
                }
                else
                {
                    backoff.Reset();
                    delay = options.Value.GapFillInterval;
                }
            }
            // Any provider failure, including an unexpected response shape, is logged and retried:
            // an exception escaping a background service would stop the whole API.
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                delay = backoff.NextDelay((error as ProviderRateLimitedException)?.RetryAfter);
                LogBackfillFailed(logger, error, delay);
            }

            await Task.Delay(delay, timeProvider, stoppingToken);
        }
    }

    /// <summary>
    /// Fills every coin and interval it can. Returns the earliest day still waiting for a USDT rate
    /// (those fills are skipped and retried soon), or null when everything was filled.
    /// </summary>
    private async Task<DateOnly?> FillGapsAsync(CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var storedSpans = await LoadStoredSpansAsync(cancellationToken);
        DateOnly? earliestWaitingDay = null;

        foreach (var coin in catalog.ActiveOf(AssetClass.Crypto))
        {
            if (coin.ProviderRefs.BinanceSymbol is not { } binanceSymbol)
            {
                continue;
            }

            foreach (var interval in Intervals)
            {
                var intervalLength = BinanceMarketDataClient.Length(interval);
                var windowStartUtc = nowUtc - CandleWriter.RetentionWindow(interval);
                var storedSpan = storedSpans.TryGetValue((coin.Id, interval), out var span) ? span : ((DateTime, DateTime)?)null;
                var fromUtc = FillStartUtc(storedSpan, windowStartUtc, intervalLength);

                if (nowUtc - fromUtc < intervalLength)
                {
                    continue;
                }

                var usdtCandles = await binance.FetchCandlesAsync(coin.Key, binanceSymbol, interval, fromUtc, nowUtc, cancellationToken);
                IReadOnlyList<CandleRow> usdCandles;
                try
                {
                    usdCandles = await usdtRates.ConvertAsync(usdtCandles, cancellationToken);
                }
                // Expected while the fiat worker is still loading the USDT history; the other
                // intervals (which need only recent rates) still fill.
                catch (UsdtHistoryIncompleteException waiting)
                {
                    earliestWaitingDay = earliestWaitingDay is { } earlier && earlier < waiting.Day ? earlier : waiting.Day;
                    continue;
                }

                var writtenCount = await candleWriter.UpsertAsync(usdCandles, cancellationToken);
                LogFilled(logger, coin.Key, interval, writtenCount);
            }
        }

        return earliestWaitingDay;
    }

    private async Task<Dictionary<(Guid AssetId, CandleInterval Interval), (DateTime OldestUtc, DateTime NewestUtc)>> LoadStoredSpansAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var cryptoIds = catalog.ActiveOf(AssetClass.Crypto).Select(coin => coin.Id).ToList();

        var spans = await db.PriceCandles
            .Where(candle => cryptoIds.Contains(candle.AssetId))
            .GroupBy(candle => new { candle.AssetId, candle.Interval })
            .Select(group => new
            {
                group.Key.AssetId,
                group.Key.Interval,
                OldestUtc = group.Min(candle => candle.BucketStartUtc),
                NewestUtc = group.Max(candle => candle.BucketStartUtc),
            })
            .ToListAsync(cancellationToken);

        return spans.ToDictionary(row => (row.AssetId, row.Interval), row => (row.OldestUtc, row.NewestUtc));
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Binance backfill {Asset} {Interval}: {CandleCount} candles")]
    private static partial void LogFilled(ILogger logger, AssetKey asset, CandleInterval interval, int candleCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance kline backfill is waiting for the USDT rate history (no rate yet for {Day}); those fills are retried in {RetryDelay}")]
    private static partial void LogWaitingForUsdtHistory(ILogger logger, DateOnly day, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance kline backfill failed; retrying in {RetryDelay}")]
    private static partial void LogBackfillFailed(ILogger logger, Exception error, TimeSpan retryDelay);
}
