using Marketlens.Api.MarketData;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketlens.Api.Tests.MarketData;

public sealed class FrankfurterClientTests(MarketlensApiFactory factory) : IDisposable
{
    private static readonly HashSet<string> RecordedCurrencies = ["EUR", "JPY", "TND"];

    private readonly ProviderFixtures fixtures = new();

    private FrankfurterClient Client => new(new HttpClient(fixtures) { BaseAddress = new Uri("https://api.frankfurter.dev/") }, NullLogger<FrankfurterClient>.Instance);

    public void Dispose() => fixtures.Dispose();

    [Fact]
    public async Task FetchCurrenciesAsync_returns_only_fiat_currencies()
    {
        var currencies = await Client.FetchCurrenciesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(161, currencies.Count);
        Assert.Contains(currencies, currency => currency.Code == "TND" && currency.Name == "Tunisian Dinar");
        Assert.DoesNotContain(currencies, currency => currency.Code == "AAVE");
        Assert.DoesNotContain(currencies, currency => currency.Code == "XAU");
    }

    [Fact]
    public async Task FetchUsdPricesAsync_inverts_each_rate_and_keeps_its_own_date()
    {
        var prices = await Client.FetchUsdPricesAsync(RecordedCurrencies, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10), TestContext.Current.CancellationToken);

        var euroOnFirstDay = Assert.Single(prices, price => price.Asset == AssetKey.Parse("fiat:EUR") && price.Date == new DateOnly(2026, 10, 1));
        Assert.Equal(decimal.Round(1m / 0.88296m, 18), euroOnFirstDay.PriceUsd);
        Assert.Equal(30, prices.Count);
    }

    [Fact]
    public async Task FetchUsdPricesAsync_asks_for_usd_based_rates_over_the_range()
    {
        await Client.FetchUsdPricesAsync(RecordedCurrencies, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10), TestContext.Current.CancellationToken);

        var request = Assert.Single(fixtures.Requests);
        Assert.Equal("?base=USD&from=2026-10-01&to=2026-10-10", request.Query);
    }

    [Fact]
    public async Task FetchUsdPricesAsync_ignores_quotes_outside_the_currency_list()
    {
        var prices = await Client.FetchUsdPricesAsync(new HashSet<string> { "EUR" }, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10), TestContext.Current.CancellationToken);

        Assert.All(prices, price => Assert.Equal("EUR", price.Asset.Symbol));
    }

    [Fact]
    public async Task FetchUsdPricesAsync_prices_tether_from_the_usdt_row()
    {
        var prices = await Client.FetchUsdPricesAsync(new HashSet<string> { "USDT" }, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 8), TestContext.Current.CancellationToken);

        Assert.Equal(39, prices.Count);
        Assert.All(prices, price => Assert.Equal(AssetKey.Tether, price.Asset));
        var lastDay = Assert.Single(prices, price => price.Date == new DateOnly(2026, 11, 8));
        Assert.Equal(decimal.Round(1m / 1.0013m, 18), lastDay.PriceUsd);
    }

    [Fact]
    public async Task FetchUsdPricesAsync_refuses_an_implausible_tether_price_and_keeps_the_rest()
    {
        fixtures.Respond("/v2/rates", _ => """
            [{"date":"2026-11-07","base":"USD","quote":"USDT","rate":1.0016},
             {"date":"2026-11-08","base":"USD","quote":"USDT","rate":0.25},
             {"date":"2026-11-08","base":"USD","quote":"EUR","rate":0.86}]
            """);

        var prices = await Client.FetchUsdPricesAsync(new HashSet<string> { "USDT", "EUR" }, new DateOnly(2026, 11, 7), new DateOnly(2026, 11, 8), TestContext.Current.CancellationToken);

        Assert.Equal(2, prices.Count);
        Assert.Single(prices, price => price.Asset == AssetKey.Tether && price.Date == new DateOnly(2026, 11, 7));
        Assert.Single(prices, price => price.Asset == AssetKey.Parse("fiat:EUR"));
    }

    [Fact]
    public void ToLatestTicks_prices_each_currency_at_its_latest_day_against_the_day_before()
    {
        var euro = AssetKey.Parse("fiat:EUR");
        DailyUsdPrice[] prices =
        [
            new(euro, new DateOnly(2026, 10, 9), 1.13m),
            new(euro, new DateOnly(2026, 10, 10), 1.14m),
            new(euro, new DateOnly(2026, 10, 8), 1.12m),
        ];

        var tick = Assert.Single(FiatRatesWorker.ToLatestTicks(prices));

        Assert.Equal(1.14m, tick.PriceUsd);
        Assert.Equal(1.13m, tick.ReferencePriceUsd);
        Assert.Equal(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), tick.ObservedAtUtc);
        Assert.Equal(Freshness.Daily, tick.Freshness);
    }

    [Fact]
    public async Task At_startup_the_fiat_worker_fills_the_catalogue_and_the_quote_book()
    {
        var catalog = factory.Services.GetRequiredService<AssetCatalog>();
        var quoteBook = factory.Services.GetRequiredService<QuoteBook>();
        var tunisianDinar = AssetKey.Parse("fiat:TND");

        var isLoaded = await Eventually.IsTrueAsync(() => catalog.TryGet(tunisianDinar, out _) && quoteBook.TryGet(tunisianDinar, out _));

        Assert.True(isLoaded);
        Assert.True(quoteBook.TryGet(tunisianDinar, out var quote));
        Assert.Equal(FrankfurterClient.SourceName, quote.Source);
        Assert.Equal(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), quote.AsOfUtc);
    }

    [Fact]
    public async Task At_startup_the_fiat_worker_prices_tether_daily_without_adding_a_fiat_currency()
    {
        var catalog = factory.Services.GetRequiredService<AssetCatalog>();
        var quoteBook = factory.Services.GetRequiredService<QuoteBook>();

        var isLoaded = await Eventually.IsTrueAsync(() => quoteBook.TryGet(AssetKey.Tether, out _));

        Assert.True(isLoaded);
        Assert.True(quoteBook.TryGet(AssetKey.Tether, out var tether));
        Assert.Equal(decimal.Round(1m / 1.0013m, 18), tether.PriceUsd);
        Assert.Equal(decimal.Round(1m / 1.0016m, 18), tether.ReferencePriceUsd);
        Assert.Equal(Freshness.Daily, tether.Freshness);
        Assert.False(catalog.TryGet(AssetKey.Parse("fiat:USDT"), out _));
    }
}
