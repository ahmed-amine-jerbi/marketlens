using Marketlens.Api.Data;
using Marketlens.Api.MarketData;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marketlens.Api.Tests.MarketData;

public sealed class CandleWriterTests(MarketlensApiFactory factory)
{
    // Each test writes its own day so tests sharing the database don't see each other's rows; the days
    // sit two years back, inside the daily retention window, so the background prune leaves them alone.
    private static readonly AssetKey Ether = AssetKey.Parse("crypto:ETH");
    private static readonly DateTime TwoYearsAgo = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddYears(-2), DateTimeKind.Utc);

    [Fact]
    public async Task UpsertAsync_writing_the_same_bucket_twice_keeps_one_row()
    {
        var bucket = TwoYearsAgo.AddDays(1);
        var row = new CandleRow(Ether, CandleInterval.OneDay, bucket, 130m, 135m, 128m, 132m, 1000m, "binance");
        var writer = factory.Services.GetRequiredService<CandleWriter>();

        await writer.UpsertAsync([row], TestContext.Current.CancellationToken);
        await writer.UpsertAsync([row], TestContext.Current.CancellationToken);

        var stored = await CandlesAt(bucket);
        Assert.Single(stored);
    }

    [Fact]
    public async Task UpsertAsync_merges_into_an_existing_bucket()
    {
        var bucket = TwoYearsAgo.AddDays(2);
        var writer = factory.Services.GetRequiredService<CandleWriter>();
        var first = new CandleRow(Ether, CandleInterval.OneDay, bucket, 130m, 135m, 128m, 132m, null, "binance");
        var later = new CandleRow(Ether, CandleInterval.OneDay, bucket, 131m, 140m, 129m, 138.25m, 2500m, "binance");

        await writer.UpsertAsync([first], TestContext.Current.CancellationToken);
        await writer.UpsertAsync([later], TestContext.Current.CancellationToken);

        var stored = Assert.Single(await CandlesAt(bucket));
        Assert.Equal(130m, stored.OpenUsd);
        Assert.Equal(140m, stored.HighUsd);
        Assert.Equal(128m, stored.LowUsd);
        Assert.Equal(138.25m, stored.CloseUsd);
        Assert.Equal(2500m, stored.Volume);
    }

    [Fact]
    public async Task UpsertAsync_skips_assets_that_are_not_in_the_catalogue()
    {
        var bucket = TwoYearsAgo.AddDays(3);
        var writer = factory.Services.GetRequiredService<CandleWriter>();
        var unknown = new CandleRow(AssetKey.Parse("crypto:NOTACOIN"), CandleInterval.OneDay, bucket, 1m, 1m, 1m, 1m, null, "test");

        var writtenCount = await writer.UpsertAsync([unknown], TestContext.Current.CancellationToken);

        Assert.Equal(0, writtenCount);
    }

    [Fact]
    public async Task UpsertAsync_skips_rows_older_than_the_retention_window()
    {
        var expiredBucket = CandleWriter.OldestKeptUtc(CandleInterval.OneDay, DateTime.UtcNow).AddDays(-1);
        var writer = factory.Services.GetRequiredService<CandleWriter>();
        var expired = new CandleRow(Ether, CandleInterval.OneDay, expiredBucket, 1m, 1m, 1m, 1m, null, "test");

        var writtenCount = await writer.UpsertAsync([expired], TestContext.Current.CancellationToken);

        Assert.Equal(0, writtenCount);
        Assert.Empty(await CandlesAt(expiredBucket));
    }

    [Fact]
    public async Task A_candle_on_the_oldest_kept_day_survives_the_prune()
    {
        // The sample feed and fiat backfill write midnight buckets; the prune once cut at the time of
        // day and deleted the oldest of them while they were being written.
        var oldestKeptBucket = CandleWriter.OldestKeptUtc(CandleInterval.OneDay, DateTime.UtcNow);
        var writer = factory.Services.GetRequiredService<CandleWriter>();
        await writer.UpsertAsync(
            [new CandleRow(Ether, CandleInterval.OneDay, oldestKeptBucket, 1m, 1m, 1m, 1m, null, "test")],
            TestContext.Current.CancellationToken);

        await writer.PruneAsync(TestContext.Current.CancellationToken);

        Assert.Single(await CandlesAt(oldestKeptBucket));
    }

    [Fact]
    public async Task Upserts_at_the_edge_of_the_window_and_prunes_running_together_never_deadlock()
    {
        var writer = factory.Services.GetRequiredService<CandleWriter>();
        var catalog = factory.Services.GetRequiredService<AssetCatalog>();
        var oldestKeptBucket = CandleWriter.OldestKeptUtc(CandleInterval.OneDay, DateTime.UtcNow);
        var edgeRows = catalog.ActiveOf(AssetClass.Crypto)
            .SelectMany(coin => Enumerable.Range(0, 30).Select(day =>
                new CandleRow(coin.Key, CandleInterval.OneDay, oldestKeptBucket.AddDays(day), 1m, 1m, 1m, 1m, null, "test")))
            .ToList();

        var writes = Enumerable.Range(0, 4).Select(_ => writer.UpsertAsync(edgeRows, TestContext.Current.CancellationToken));
        var prunes = Enumerable.Range(0, 4).Select(_ => writer.PruneAsync(TestContext.Current.CancellationToken));

        var completed = await Task.WhenAll(writes.Concat(prunes));
        Assert.All(completed.Take(4), writtenCount => Assert.Equal(edgeRows.Count, writtenCount));
    }

    private async Task<List<PriceCandle>> CandlesAt(DateTime bucket)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var catalog = factory.Services.GetRequiredService<AssetCatalog>();
        Assert.True(catalog.TryGet(Ether, out var ether));

        return await db.PriceCandles
            .Where(candle => candle.AssetId == ether.Id && candle.Interval == CandleInterval.OneDay && candle.BucketStartUtc == bucket)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
