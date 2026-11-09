using Marketlens.Api.MarketData;

using Microsoft.Extensions.Time.Testing;

namespace Marketlens.Api.Tests.MarketData;

public sealed class QuoteBookTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 15, 0, 0, DateTimeKind.Utc);
    private static readonly AssetKey Bitcoin = AssetKey.Parse("crypto:BTC");

    private readonly QuoteBook quoteBook = new(new FakeTimeProvider(new DateTimeOffset(Now)));

    [Fact]
    public void Apply_stores_the_first_tick_for_an_asset()
    {
        var isApplied = quoteBook.Apply(Tick(76036.00m, Now));

        Assert.True(isApplied);
        Assert.True(quoteBook.TryGet(Bitcoin, out var quote));
        Assert.Equal(76036.00m, quote.PriceUsd);
        Assert.Equal(75873.47m, quote.ReferencePriceUsd);
        Assert.Equal(Freshness.Live, quote.Freshness);
    }

    [Fact]
    public void Apply_replaces_the_quote_with_a_newer_tick()
    {
        quoteBook.Apply(Tick(76036.00m, Now));

        var isApplied = quoteBook.Apply(Tick(76040.12m, Now.AddSeconds(1)));

        Assert.True(isApplied);
        Assert.True(quoteBook.TryGet(Bitcoin, out var quote));
        Assert.Equal(76040.12m, quote.PriceUsd);
    }

    [Fact]
    public void Apply_ignores_a_tick_that_arrives_out_of_order()
    {
        quoteBook.Apply(Tick(76040.12m, Now.AddSeconds(1)));

        var isApplied = quoteBook.Apply(Tick(76036.00m, Now));

        Assert.False(isApplied);
        Assert.True(quoteBook.TryGet(Bitcoin, out var quote));
        Assert.Equal(76040.12m, quote.PriceUsd);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Apply_rejects_a_non_positive_price(int price)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => quoteBook.Apply(Tick(price, Now)));
    }

    [Fact]
    public void TryGet_always_prices_the_usd_pivot_at_one_and_current()
    {
        Assert.True(quoteBook.TryGet(AssetKey.UsDollar, out var quote));

        Assert.Equal(1m, quote.PriceUsd);
        Assert.Equal(Now, quote.AsOfUtc);
        Assert.Equal(Freshness.Live, quote.Freshness);
    }

    [Fact]
    public void Apply_never_overrides_the_usd_pivot()
    {
        var isApplied = quoteBook.Apply(new PriceTick(AssetKey.UsDollar, 1.01m, null, Now, "test", Freshness.Daily));

        Assert.False(isApplied);
        Assert.True(quoteBook.TryGet(AssetKey.UsDollar, out var quote));
        Assert.Equal(1m, quote.PriceUsd);
    }

    [Fact]
    public void Apply_keeps_the_known_reference_when_a_trade_brings_none()
    {
        quoteBook.Apply(Tick(76036.00m, Now));

        quoteBook.Apply(new PriceTick(Bitcoin, 76050.00m, ReferencePriceUsd: null, Now.AddSeconds(1), "binance", Freshness.Live));

        Assert.True(quoteBook.TryGet(Bitcoin, out var quote));
        Assert.Equal(76050.00m, quote.PriceUsd);
        Assert.Equal(75873.47m, quote.ReferencePriceUsd);
    }

    [Fact]
    public void SetReferencePrice_replaces_the_reference_without_touching_the_price()
    {
        quoteBook.Apply(Tick(76036.00m, Now));

        quoteBook.SetReferencePrice(Bitcoin, 74000.00m);

        Assert.True(quoteBook.TryGet(Bitcoin, out var quote));
        Assert.Equal(76036.00m, quote.PriceUsd);
        Assert.Equal(74000.00m, quote.ReferencePriceUsd);
    }

    [Fact]
    public void Apply_announces_accepted_ticks_and_stays_quiet_about_ignored_ones()
    {
        var announced = new List<UsdQuote>();
        quoteBook.QuoteUpdated += announced.Add;

        quoteBook.Apply(Tick(76036.00m, Now.AddSeconds(1)));
        quoteBook.Apply(Tick(76000.00m, Now));

        Assert.Equal(76036.00m, Assert.Single(announced).PriceUsd);
    }

    [Fact]
    public void Restore_brings_back_saved_quotes_silently_without_overriding_newer_prices()
    {
        var announced = new List<UsdQuote>();
        quoteBook.Apply(Tick(76040.12m, Now));
        quoteBook.QuoteUpdated += announced.Add;
        var tunisianDinar = AssetKey.Parse("fiat:TND");

        quoteBook.Restore(
        [
            new UsdQuote(Bitcoin, 70000m, null, Now.AddHours(-1), "binance", Freshness.Live),
            new UsdQuote(tunisianDinar, 0.334m, 0.333m, Now.AddDays(-1), "frankfurter", Freshness.Daily),
        ]);

        Assert.Empty(announced);
        Assert.True(quoteBook.TryGet(Bitcoin, out var bitcoin));
        Assert.Equal(76040.12m, bitcoin.PriceUsd);
        Assert.True(quoteBook.TryGet(tunisianDinar, out var dinar));
        Assert.Equal(0.334m, dinar.PriceUsd);
    }

    [Fact]
    public void RemoveBySource_drops_only_that_sources_quotes_so_an_older_real_price_can_take_their_place()
    {
        var microsoft = AssetKey.Parse("stock:MSFT");
        var apple = AssetKey.Parse("stock:AAPL");
        quoteBook.Apply(new PriceTick(microsoft, 155.38m, null, Now, "sample", Freshness.Delayed));
        quoteBook.Apply(new PriceTick(apple, 350.51m, null, Now, "finnhub", Freshness.Live));
        var announced = new List<UsdQuote>();
        quoteBook.QuoteUpdated += announced.Add;

        var removedCount = quoteBook.RemoveBySource("sample");
        var isRealPriceApplied = quoteBook.Apply(new PriceTick(microsoft, 497.10m, 495.00m, Now.AddMinutes(-11), "finnhub", Freshness.Delayed));

        Assert.Equal(1, removedCount);
        Assert.True(isRealPriceApplied);
        Assert.True(quoteBook.TryGet(microsoft, out var microsoftQuote));
        Assert.Equal("finnhub", microsoftQuote.Source);
        Assert.True(quoteBook.TryGet(apple, out _));
        Assert.Equal(497.10m, Assert.Single(announced).PriceUsd);
    }

    [Fact]
    public void TryGet_returns_false_for_an_asset_without_a_price()
    {
        Assert.False(quoteBook.TryGet(AssetKey.Parse("fiat:TND"), out _));
    }

    private static PriceTick Tick(decimal priceUsd, DateTime observedAtUtc) =>
        new(Bitcoin, priceUsd, ReferencePriceUsd: 75873.47m, observedAtUtc, "binance", Freshness.Live);
}
