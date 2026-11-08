using System.Text.Json;

using Marketlens.Api.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marketlens.Api.MarketData;

/// <summary>
/// Fills crypto candle history from Binance klines: on a fresh database, everything inside each
/// interval's retention window; afterwards, only the gap since the newest stored bucket (downtime,
/// a dropped stream). Keyless, so crypto charts and forecasts work with no API keys (D28).
/// </summary>
public sealed partial class BinanceKlinesBackfill(
    BinanceMarketDataClient binance,
    AssetCatalog catalog,
    CandleWriter candleWriter,
    IServiceScopeFactory scopeFactory,
    IOptions<BinanceOptions> options,
    TimeProvider timeProvider,
    ILogger<BinanceKlinesBackfill> logger) : BackgroundService
{
    private static readonly CandleInterval[] Intervals = [CandleInterval.OneDay, CandleInterval.OneHour, CandleInterval.OneMinute];

    private readonly ProviderBackoff backoff = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await FillGapsAsync(stoppingToken);
                backoff.Reset();
                delay = options.Value.GapFillInterval;
            }
            catch (Exception error) when (error is HttpRequestException or JsonException && !stoppingToken.IsCancellationRequested)
            {
                delay = backoff.NextDelay((error as ProviderRateLimitedException)?.RetryAfter);
                LogBackfillFailed(logger, error, delay);
            }

            await Task.Delay(delay, timeProvider, stoppingToken);
        }
    }

    private async Task FillGapsAsync(CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var newestBuckets = await LoadNewestBucketsAsync(cancellationToken);

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
                var fromUtc = newestBuckets.TryGetValue((coin.Id, interval), out var newestUtc) && newestUtc > windowStartUtc
                    ? newestUtc
                    : windowStartUtc;

                if (nowUtc - fromUtc < intervalLength)
                {
                    continue;
                }

                var candles = await binance.FetchCandlesAsync(coin.Key, binanceSymbol, interval, fromUtc, nowUtc, cancellationToken);
                var writtenCount = await candleWriter.UpsertAsync(candles, cancellationToken);
                LogFilled(logger, coin.Key, interval, writtenCount);
            }
        }
    }

    private async Task<Dictionary<(Guid AssetId, CandleInterval Interval), DateTime>> LoadNewestBucketsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var cryptoIds = catalog.ActiveOf(AssetClass.Crypto).Select(coin => coin.Id).ToList();

        var newest = await db.PriceCandles
            .Where(candle => cryptoIds.Contains(candle.AssetId))
            .GroupBy(candle => new { candle.AssetId, candle.Interval })
            .Select(group => new { group.Key.AssetId, group.Key.Interval, NewestUtc = group.Max(candle => candle.BucketStartUtc) })
            .ToListAsync(cancellationToken);

        return newest.ToDictionary(row => (row.AssetId, row.Interval), row => row.NewestUtc);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Binance backfill {Asset} {Interval}: {CandleCount} candles")]
    private static partial void LogFilled(ILogger logger, AssetKey asset, CandleInterval interval, int candleCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance kline backfill failed; retrying in {RetryDelay}")]
    private static partial void LogBackfillFailed(ILogger logger, Exception error, TimeSpan retryDelay);
}
