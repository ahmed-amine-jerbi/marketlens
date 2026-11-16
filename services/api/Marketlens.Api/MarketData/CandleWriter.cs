using System.Collections.Concurrent;

using Marketlens.Api.Data;

using Microsoft.EntityFrameworkCore;

using Npgsql;

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
/// Once a day, candles older than their interval's retention window are deleted. Upserts and the
/// prune share one cut-off (<see cref="OldestKeptUtc"/>), so a writer never inserts a row the prune is
/// deleting; two statements locking the same rows in opposite orders is how they deadlocked.
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

    /// <summary>Lets the startup backfills finish before the first prune competes with them for rows.</summary>
    private static readonly TimeSpan FirstPruneDelay = TimeSpan.FromMinutes(5);

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

    /// <summary>
    /// The start of the oldest bucket an interval keeps at <paramref name="nowUtc"/>: rows starting
    /// earlier are never written and are pruned.
    /// </summary>
    public static DateTime OldestKeptUtc(CandleInterval interval, DateTime nowUtc) =>
        BucketStart(nowUtc - RetentionWindow(interval), interval);

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

    /// <returns>
    /// The number of rows written; rows for assets not in the catalogue, and rows older than their
    /// interval's retention window, are skipped.
    /// </returns>
    public async Task<int> UpsertAsync(IEnumerable<CandleRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;

        var keptRows = new List<(Guid AssetId, CandleRow Row)>();
        foreach (var row in rows)
        {
            if (row.BucketStartUtc >= OldestKeptUtc(row.Interval, nowUtc) && catalog.TryGet(row.Asset, out var entry))
            {
                keptRows.Add((entry.Id, row));
            }
        }

        // Sorted by the primary key so concurrent writers lock shared rows in the same order.
        var orderedRows = keptRows
            .OrderBy(candidate => candidate.AssetId)
            .ThenBy(candidate => candidate.Row.Interval)
            .ThenBy(candidate => candidate.Row.BucketStartUtc);

        var writtenCount = 0;
        foreach (var batch in orderedRows.Chunk(BatchSize))
        {
            writtenCount += await RetryOnceOnDeadlockAsync(() => UpsertBatchAsync(db, batch, cancellationToken));
        }

        return writtenCount;
    }

    /// <summary>The newest bucket start per asset among the candles a source wrote for an interval.</summary>
    public async Task<IReadOnlyDictionary<Guid, DateTime>> NewestBucketStartsAsync(string source, CandleInterval interval, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        return await db.PriceCandles
            .Where(candle => candle.Source == source && candle.Interval == interval)
            .GroupBy(candle => candle.AssetId)
            .Select(group => new { AssetId = group.Key, NewestUtc = group.Max(candle => candle.BucketStartUtc) })
            .ToDictionaryAsync(row => row.AssetId, row => row.NewestUtc, cancellationToken);
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
            var cutoffUtc = OldestKeptUtc(interval, nowUtc);
            deletedCount += await RetryOnceOnDeadlockAsync(() => db.PriceCandles
                .Where(candle => candle.Interval == interval && candle.BucketStartUtc < cutoffUtc)
                .ExecuteDeleteAsync(cancellationToken));
        }

        return deletedCount;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPruneUtc = timeProvider.GetUtcNow().UtcDateTime + FirstPruneDelay;
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

    // A deadlock victim is rolled back whole, so running the same statement again is safe. Ordered
    // writes and the shared cut-off make it rare; one retry covers what remains.
    private async Task<int> RetryOnceOnDeadlockAsync(Func<Task<int>> statement)
    {
        try
        {
            return await statement();
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            LogDeadlockRetried(logger);
            return await statement();
        }
    }

    private static Task<int> UpsertBatchAsync(MarketlensDbContext db, (Guid AssetId, CandleRow Row)[] batch, CancellationToken cancellationToken)
    {
        var assetIds = batch.Select(candidate => candidate.AssetId).ToArray();
        var intervals = batch.Select(candidate => PriceCandleConfiguration.ToColumnValue(candidate.Row.Interval)).ToArray();
        var bucketStarts = batch.Select(candidate => DateTime.SpecifyKind(candidate.Row.BucketStartUtc, DateTimeKind.Utc)).ToArray();
        var opens = batch.Select(candidate => candidate.Row.OpenUsd).ToArray();
        var highs = batch.Select(candidate => candidate.Row.HighUsd).ToArray();
        var lows = batch.Select(candidate => candidate.Row.LowUsd).ToArray();
        var closes = batch.Select(candidate => candidate.Row.CloseUsd).ToArray();
        var volumes = batch.Select(candidate => candidate.Row.Volume).ToArray();
        var sources = batch.Select(candidate => candidate.Row.Source).ToArray();

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "A candle statement was chosen as a deadlock victim; running it once more")]
    private static partial void LogDeadlockRetried(ILogger logger);
}
