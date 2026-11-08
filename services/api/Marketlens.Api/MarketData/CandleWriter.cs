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
/// The only writer of <c>price_candles</c>. Upserts are idempotent on (asset, interval, bucket) and
/// merge into an existing bucket: the first open is kept, high and low widen, close and volume take
/// the newer value. That makes re-running a backfill, or a live bucket meeting a backfilled one, safe.
/// </summary>
public sealed class CandleWriter(AssetCatalog catalog, IServiceScopeFactory scopeFactory)
{
    /// <summary>Rows per INSERT; keeps each statement's parameter arrays a few hundred KB.</summary>
    private const int BatchSize = 5000;

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
}
