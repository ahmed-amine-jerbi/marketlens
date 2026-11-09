using Marketlens.Api.Data;
using Marketlens.Api.MarketData;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marketlens.Api.Tests.MarketData;

public sealed class LiveCandlesAndSnapshotsTests(MarketlensApiFactory factory)
{
    private static readonly AssetKey Cardano = AssetKey.Parse("crypto:ADA");
    private static readonly AssetKey Polkadot = AssetKey.Parse("crypto:DOT");

    [Fact]
    public async Task Live_prices_in_one_minute_become_one_minute_candle()
    {
        var writer = factory.Services.GetRequiredService<CandleWriter>();
        var minuteStart = CandleWriter.BucketStart(DateTime.UtcNow.AddHours(-2), CandleInterval.OneMinute);

        writer.Record(Quote(Cardano, 0.5012m, minuteStart.AddSeconds(5)));
        writer.Record(Quote(Cardano, 0.5101m, minuteStart.AddSeconds(20)));
        writer.Record(Quote(Cardano, 0.4987m, minuteStart.AddSeconds(40)));
        writer.Record(Quote(Cardano, 0.5050m, minuteStart.AddSeconds(55)));
        await writer.FlushAsync(TestContext.Current.CancellationToken);

        var candle = Assert.Single(await CandlesAsync(Cardano, CandleInterval.OneMinute, minuteStart));
        Assert.Equal(0.5012m, candle.OpenUsd);
        Assert.Equal(0.5101m, candle.HighUsd);
        Assert.Equal(0.4987m, candle.LowUsd);
        Assert.Equal(0.5050m, candle.CloseUsd);
    }

    [Fact]
    public async Task Daily_reference_rates_never_become_minute_candles()
    {
        var writer = factory.Services.GetRequiredService<CandleWriter>();
        var minuteStart = CandleWriter.BucketStart(DateTime.UtcNow.AddHours(-3), CandleInterval.OneMinute);

        writer.Record(Quote(Cardano, 0.51m, minuteStart, Freshness.Daily));
        await writer.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await CandlesAsync(Cardano, CandleInterval.OneMinute, minuteStart));
    }

    [Fact]
    public async Task PruneAsync_deletes_minute_candles_older_than_three_days_and_keeps_newer_ones()
    {
        var writer = factory.Services.GetRequiredService<CandleWriter>();
        var expired = CandleWriter.BucketStart(DateTime.UtcNow.AddDays(-10), CandleInterval.OneMinute);
        var kept = CandleWriter.BucketStart(DateTime.UtcNow.AddHours(-4), CandleInterval.OneMinute);
        await writer.UpsertAsync(
            [MinuteCandle(Cardano, expired), MinuteCandle(Cardano, kept)],
            TestContext.Current.CancellationToken);

        await writer.PruneAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await CandlesAsync(Cardano, CandleInterval.OneMinute, expired));
        Assert.Single(await CandlesAsync(Cardano, CandleInterval.OneMinute, kept));
    }

    [Fact]
    public async Task Saved_quotes_come_back_after_a_restart_with_their_age_and_freshness()
    {
        var observedAtUtc = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        factory.Services.GetRequiredService<QuoteBook>().Apply(new PriceTick(Polkadot, 4.1234m, 4.0001m, observedAtUtc, "binance", Freshness.Live));
        await factory.Services.GetRequiredService<QuoteSnapshots>().SaveAsync(TestContext.Current.CancellationToken);

        var bookAfterRestart = new QuoteBook(TimeProvider.System);
        var snapshotsAfterRestart = ActivatorUtilities.CreateInstance<QuoteSnapshots>(factory.Services, bookAfterRestart);
        await snapshotsAfterRestart.RestoreAsync(TestContext.Current.CancellationToken);

        Assert.True(bookAfterRestart.TryGet(Polkadot, out var restored));
        Assert.Equal(4.1234m, restored.PriceUsd);
        Assert.Equal(4.0001m, restored.ReferencePriceUsd);
        Assert.Equal(Freshness.Live, restored.Freshness);
        Assert.True(restored.AsOfUtc >= observedAtUtc);
    }

    [Fact]
    public async Task Backfilled_crypto_candles_are_stored_in_usd_at_that_days_usdt_rate()
    {
        var bitcoin = AssetKey.Parse("crypto:BTC");
        var recordedDay = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
        var expectedCloseUsd = decimal.Round(75867.59m * decimal.Round(1m / 1.0016m, 18), 18);

        var isStoredInUsd = await Eventually.IsTrueAsync(async () =>
            (await CandlesAsync(bitcoin, CandleInterval.OneDay, recordedDay)).SingleOrDefault()?.CloseUsd == expectedCloseUsd);

        Assert.True(isStoredInUsd);
    }

    [Fact]
    public async Task A_backfill_restores_the_whole_window_when_only_todays_live_bucket_is_stored()
    {
        var chainlink = AssetKey.Parse("crypto:LINK");
        var recordedDay = new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc);
        var todayUtc = DateTime.UtcNow.Date;
        var writer = factory.Services.GetRequiredService<CandleWriter>();
        await Eventually.IsTrueAsync(async () => (await CandlesAsync(chainlink, CandleInterval.OneDay, recordedDay)).Count == 1);
        await DeleteCandlesAsync(chainlink, CandleInterval.OneDay);
        await writer.UpsertAsync([new CandleRow(chainlink, CandleInterval.OneDay, todayUtc, 15m, 15m, 15m, 15m, Volume: null, "binance")], TestContext.Current.CancellationToken);

        using var backfill = ActivatorUtilities.CreateInstance<BinanceKlinesBackfill>(factory.Services);
        await backfill.StartAsync(TestContext.Current.CancellationToken);
        var isRestored = await Eventually.IsTrueAsync(async () => (await CandlesAsync(chainlink, CandleInterval.OneDay, recordedDay)).Count == 1);
        await backfill.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(isRestored);
        var restored = Assert.Single(await CandlesAsync(chainlink, CandleInterval.OneDay, recordedDay));
        Assert.Equal(decimal.Round(75867.59m * decimal.Round(1m / 1.0016m, 18), 18), restored.CloseUsd);
    }

    private static UsdQuote Quote(AssetKey asset, decimal priceUsd, DateTime asOfUtc, Freshness freshness = Freshness.Live) =>
        new(asset, priceUsd, ReferencePriceUsd: null, asOfUtc, "test", freshness);

    private static CandleRow MinuteCandle(AssetKey asset, DateTime bucketStartUtc) =>
        new(asset, CandleInterval.OneMinute, bucketStartUtc, 1m, 1m, 1m, 1m, Volume: null, "test");

    private async Task<List<PriceCandle>> CandlesAsync(AssetKey asset, CandleInterval interval, DateTime bucketStartUtc)
    {
        Assert.True(factory.Services.GetRequiredService<AssetCatalog>().TryGet(asset, out var entry));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        return await db.PriceCandles
            .Where(candle => candle.AssetId == entry.Id && candle.Interval == interval && candle.BucketStartUtc == bucketStartUtc)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task DeleteCandlesAsync(AssetKey asset, CandleInterval interval)
    {
        Assert.True(factory.Services.GetRequiredService<AssetCatalog>().TryGet(asset, out var entry));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        await db.PriceCandles
            .Where(candle => candle.AssetId == entry.Id && candle.Interval == interval)
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }
}
