using System.Collections.Concurrent;

using Marketlens.Api.Data;

using Microsoft.EntityFrameworkCore;

namespace Marketlens.Api.MarketData;

/// <summary>One candle in USD, keyed by asset key rather than database id.</summary>
public sealed record CandleRow(
    AssetKey Asset,
    CandleInterval Interval,
    DateTime BucketStartUtc,
    decimal OpenUsd,
    decimal HighUsd,
    decimal LowUsd,
    decimal CloseUsd,
    decimal? Volume,
    string Source);

/// <summary>
/// The only writer of <c>price_candles</c>. Live prices from the quote book are rolled into
/// 1-minute, 1-hour and 1-day buckets in memory and flushed every few seconds; providers' history
/// arrives through <see cref="UpsertAsync"/>. Upserts are idempotent on (asset, interval, bucket) and
/// merge into an existing bucket: the first open is kept, high and low widen, close and volume take
/// the newer value. That makes re-running a backfill, or a live bucket meeting a backfilled one, safe.
/// Once a day, candles older than their interval's retention window are deleted.
/// </summary>
public sealed partial class CandleWriter(
    AssetCatalog catalog,
    QuoteBook quoteBook,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<CandleWriter> logger) : BackgroundService
{
    /// <summary>Rows per INSERT; keeps each statement's parameter arrays a few hundred KB.</summary>
    private const int BatchSize = 5000;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PruneInterval = TimeSpan.FromDays(1);
    private static readonly CandleInterval[] Intervals = [CandleInterval.OneMinute, CandleInterval.OneHour, CandleInterval.OneDay];

    private readonly ConcurrentDictionary<(AssetKey Asset, CandleInterval Interval, DateTime BucketStartUtc), CandleRow> pendingBuckets = new();

    /// <summary>
    /// How far back each interval is kept (plan, data model): minutes for the 1-day chart, hours for
    /// up to three months, days for the long ranges and forecasts.
    /// </summary>
    public static TimeSpan RetentionWindow(CandleInterval interval) => interval switch
    {
        CandleInterval.OneMinute => TimeSpan.FromDays(3),
        CandleInterval.OneHour => TimeSpan.FromDays(120),
        CandleInterval.OneDay => TimeSpan.FromDays(5 * 365 + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, null),
    };

    public static DateTime BucketStart(DateTime atUtc, CandleInterval interval) => interval switch
    {
        CandleInterval.OneMinute => new DateTime(atUtc.Year, atUtc.Month, atUtc.Day, atUtc.Hour, atUtc.Minute, 0, DateTimeKind.Utc),
        CandleInterval.OneHour => new DateTime(atUtc.Year, atUtc.Month, atUtc.Day, atUtc.Hour, 0, 0, DateTimeKind.Utc),
        CandleInterval.OneDay => new DateTime(atUtc.Year, atUtc.Month, atUtc.Day, 0, 0, 0, DateTimeKind.Utc),
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, null),
    };

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        quoteBook.QuoteUpdated += Record;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        quoteBook.QuoteUpdated -= Record;
        await base.StopAsync(cancellationToken);
        try
        {
            await FlushAsync(cancellationToken);
        }
        // The final flush is best effort: during shutdown the database or the service container may
        // already be gone; at most the last few seconds of live buckets are lost.
        catch (Exception error)
        {
            LogFlushFailed(logger, error);
        }
    }

    /// <summary>
    /// Rolls a live or delayed price into its pending buckets. Daily reference rates are skipped:
    /// minute candles of a once-a-day price would mislead, and their daily candles come from the
    /// provider directly.
    /// </summary>
    public void Record(UsdQuote quote)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (quote.Freshness == Freshness.Daily)
        {
            return;
        }

        foreach (var interval in Intervals)
        {
            var bucketStartUtc = BucketStart(quote.AsOfUtc, interval);
            pendingBuckets.AddOrUpdate(
                (quote.Asset, interval, bucketStartUtc),
                _ => new CandleRow(quote.Asset, interval, bucketStartUtc, quote.PriceUsd, quote.PriceUsd, quote.PriceUsd, quote.PriceUsd, Volume: null, quote.Source),
                (_, pending) => pending with
                {
                    HighUsd = Math.Max(pending.HighUsd, quote.PriceUsd),
                    LowUsd = Math.Min(pending.LowUsd, quote.PriceUsd),
                    CloseUsd = quote.PriceUsd,
                    Source = quote.Source,
                });
        }
    }

    /// <summary>Writes the pending live buckets. Called every few seconds and on shutdown.</summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        var flushed = new List<CandleRow>();
        foreach (var key in pendingBuckets.Keys)
        {
            if (pendingBuckets.TryRemove(key, out var bucket))
            {
                flushed.Add(bucket);
            }
        }

        return flushed.Count == 0 ? 0 : await UpsertAsync(flushed, cancellationToken);
    }

    /// <returns>The number of rows written; rows for assets not in the catalogue are skipped.</returns>
    public async Task<int> UpsertAsync(IEnumerable<CandleRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();

        var writtenCount = 0;
        foreach (var batch in rows.Where(row => catalog.TryGet(row.Asset, out _)).Chunk(BatchSize))
        {
            writtenCount += await UpsertBatchAsync(db, batch, cancellationToken);
        }

        return writtenCount;
    }

    /// <summary>Deletes every candle a source wrote (the sample feed, when real stock data takes over).</summary>
    public async Task<int> DeleteBySourceAsync(string source, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        return await db.PriceCandles.Where(candle => candle.Source == source).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Deletes candles older than their interval's retention window.</summary>
    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var deletedCount = 0;
        foreach (var interval in Intervals)
        {
            var cutoffUtc = nowUtc - RetentionWindow(interval);
            deletedCount += await db.PriceCandles
                .Where(candle => candle.Interval == interval && candle.BucketStartUtc < cutoffUtc)
                .ExecuteDeleteAsync(cancellationToken);
        }

        return deletedCount;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPruneUtc = timeProvider.GetUtcNow().UtcDateTime;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FlushAsync(stoppingToken);
                if (timeProvider.GetUtcNow().UtcDateTime >= nextPruneUtc)
                {
                    var prunedCount = await PruneAsync(stoppingToken);
                    LogPruned(logger, prunedCount);
                    nextPruneUtc = timeProvider.GetUtcNow().UtcDateTime + PruneInterval;
                }
            }
            // A database failure is logged and retried on the next pass; an exception escaping a
            // background service would stop the whole API. Buckets of a failed flush are lost, but the
            // next live prices and the backfills cover the gap.
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                LogFlushFailed(logger, error);
            }

            await Task.Delay(FlushInterval, timeProvider, stoppingToken);
        }
    }

    private Task<int> UpsertBatchAsync(MarketlensDbContext db, CandleRow[] batch, CancellationToken cancellationToken)
    {
        var assetIds = batch.Select(row => catalog.TryGet(row.Asset, out var entry) ? entry.Id : Guid.Empty).ToArray();
        var intervals = batch.Select(row => PriceCandleConfiguration.ToColumnValue(row.Interval)).ToArray();
        var bucketStarts = batch.Select(row => DateTime.SpecifyKind(row.BucketStartUtc, DateTimeKind.Utc)).ToArray();
        var opens = batch.Select(row => row.OpenUsd).ToArray();
        var highs = batch.Select(row => row.HighUsd).ToArray();
        var lows = batch.Select(row => row.LowUsd).ToArray();
        var closes = batch.Select(row => row.CloseUsd).ToArray();
        var volumes = batch.Select(row => row.Volume).ToArray();
        var sources = batch.Select(row => row.Source).ToArray();

        // Parameterised arrays unnested into rows: one round trip per batch, no string-built SQL.
        return db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO price_candles (asset_id, interval, bucket_start_utc, open_usd, high_usd, low_usd, close_usd, volume, source)
            SELECT * FROM unnest({assetIds}, {intervals}, {bucketStarts}, {opens}, {highs}, {lows}, {closes}, {volumes}, {sources})
            ON CONFLICT (asset_id, interval, bucket_start_utc) DO UPDATE SET
                high_usd = GREATEST(price_candles.high_usd, EXCLUDED.high_usd),
                low_usd = LEAST(price_candles.low_usd, EXCLUDED.low_usd),
                close_usd = EXCLUDED.close_usd,
                volume = COALESCE(EXCLUDED.volume, price_candles.volume),
                source = EXCLUDED.source
            """,
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {CandleCount} candles past their retention window")]
    private static partial void LogPruned(ILogger logger, int candleCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing live candles failed; retrying on the next pass")]
    private static partial void LogFlushFailed(ILogger logger, Exception error);
}
