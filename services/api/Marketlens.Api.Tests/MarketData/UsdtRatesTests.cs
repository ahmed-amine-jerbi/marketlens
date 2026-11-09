using System.Globalization;

using Marketlens.Api.Data;
using Marketlens.Api.MarketData;

using Microsoft.Extensions.DependencyInjection;

namespace Marketlens.Api.Tests.MarketData;

public sealed class UsdtRatesTests(MarketlensApiFactory factory)
{
    private static readonly AssetKey Bitcoin = AssetKey.Parse("crypto:BTC");

    private static readonly DailyUsdPrice[] RatesByDay =
    [
        new(AssetKey.Tether, new DateOnly(2026, 11, 1), 0.9985m),
        new(AssetKey.Tether, new DateOnly(2026, 11, 2), 0.9982m),
        new(AssetKey.Tether, new DateOnly(2026, 11, 19), 0.9991m),
    ];

    [Fact]
    public void ToUsd_converts_a_tick_and_its_24_hour_reference_and_keeps_the_rest()
    {
        var usdtTick = new PriceTick(Bitcoin, 75000m, 74000m, new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc), "binance", Freshness.Live);

        var usdTick = UsdtRates.ToUsd(usdtTick, 0.9987m);

        Assert.Equal(74902.5m, usdTick.PriceUsd);
        Assert.Equal(73903.8m, usdTick.ReferencePriceUsd);
        Assert.Equal(usdtTick with { PriceUsd = usdTick.PriceUsd, ReferencePriceUsd = usdTick.ReferencePriceUsd }, usdTick);
    }

    [Fact]
    public void ToUsd_converts_a_candles_prices_but_not_its_volume_in_coins()
    {
        var usdtCandle = new CandleRow(Bitcoin, CandleInterval.OneDay, new DateTime(2026, 11, 8, 0, 0, 0, DateTimeKind.Utc), 100m, 120m, 90m, 110m, 12.5m, "binance");

        var usdCandle = UsdtRates.ToUsd(usdtCandle, 0.998m);

        Assert.Equal((99.8m, 119.76m, 89.82m, 109.78m), (usdCandle.OpenUsd, usdCandle.HighUsd, usdCandle.LowUsd, usdCandle.CloseUsd));
        Assert.Equal(12.5m, usdCandle.Volume);
    }

    [Theory]
    [InlineData(2026, 11, 2, "0.9982")]
    [InlineData(2026, 11, 4, "0.9982")]
    [InlineData(2026, 11, 9, "0.9982")]
    [InlineData(2026, 11, 20, "0.9991")]
    [InlineData(2026, 12, 31, "0.9991")]
    public void RateOn_uses_the_days_rate_the_latest_before_it_or_after_the_last_rate_the_newest(int year, int month, int day, string expectedRate)
    {
        Assert.Equal(decimal.Parse(expectedRate, CultureInfo.InvariantCulture), UsdtRates.RateOn(RatesByDay, new DateOnly(year, month, day)));
    }

    [Theory]
    [InlineData(2026, 10, 31)]
    [InlineData(2026, 11, 10)]
    [InlineData(2026, 11, 18)]
    public void RateOn_has_no_rate_before_the_first_or_inside_a_gap_of_more_than_a_week(int year, int month, int day)
    {
        Assert.Null(UsdtRates.RateOn(RatesByDay, new DateOnly(year, month, day)));
    }

    [Fact]
    public void A_live_tick_is_held_back_until_a_usdt_rate_is_known()
    {
        var quoteBook = new QuoteBook(TimeProvider.System);
        var usdtRates = new UsdtRates(quoteBook, factory.Services.GetRequiredService<AssetCatalog>(), factory.Services.GetRequiredService<IServiceScopeFactory>());
        var usdtTick = new PriceTick(Bitcoin, 75000m, null, DateTime.UtcNow, "binance", Freshness.Live);

        var isConvertedWithoutRate = usdtRates.TryConvert(usdtTick, out _);
        quoteBook.Apply(new PriceTick(AssetKey.Tether, 0.998m, null, DateTime.UtcNow, FrankfurterClient.SourceName, Freshness.Daily));
        var isConvertedWithRate = usdtRates.TryConvert(usdtTick, out var usdTick);

        Assert.False(isConvertedWithoutRate);
        Assert.True(isConvertedWithRate);
        Assert.Equal(74850m, usdTick!.PriceUsd);
    }

    [Fact]
    public async Task ConvertAsync_prices_each_candle_at_its_days_stored_usdt_rate()
    {
        var usdtRates = factory.Services.GetRequiredService<UsdtRates>();
        var quoteBook = factory.Services.GetRequiredService<QuoteBook>();
        await Eventually.IsTrueAsync(() => quoteBook.TryGet(AssetKey.Tether, out _));
        CandleRow[] usdtCandles =
        [
            new(Bitcoin, CandleInterval.OneDay, new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Utc), 1000m, 1000m, 1000m, 1000m, null, "binance"),
            new(Bitcoin, CandleInterval.OneHour, new DateTime(2026, 11, 8, 13, 0, 0, DateTimeKind.Utc), 1000m, 1000m, 1000m, 1000m, null, "binance"),
        ];

        var usdCandles = await usdtRates.ConvertAsync(usdtCandles, TestContext.Current.CancellationToken);

        Assert.Equal(decimal.Round(1000m * decimal.Round(1m / 1.0016m, 18), 18), usdCandles[0].CloseUsd);
        Assert.Equal(decimal.Round(1000m * decimal.Round(1m / 1.0013m, 18), 18), usdCandles[1].CloseUsd);
    }

    [Fact]
    public async Task ConvertAsync_waits_rather_than_convert_a_candle_from_before_the_first_stored_rate()
    {
        var usdtRates = factory.Services.GetRequiredService<UsdtRates>();
        CandleRow[] beforeAnyRecordedRate = [new(Bitcoin, CandleInterval.OneDay, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), 1m, 1m, 1m, 1m, null, "binance")];

        var error = await Assert.ThrowsAsync<UsdtHistoryIncompleteException>(() => usdtRates.ConvertAsync(beforeAnyRecordedRate, TestContext.Current.CancellationToken));

        Assert.Equal(new DateOnly(2026, 9, 1), error.Day);
    }
}
