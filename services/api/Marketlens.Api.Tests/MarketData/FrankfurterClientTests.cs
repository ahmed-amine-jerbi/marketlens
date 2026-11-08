using Marketlens.Api.MarketData;

using Microsoft.Extensions.DependencyInjection;

namespace Marketlens.Api.Tests.MarketData;

public sealed class FrankfurterClientTests(MarketlensApiFactory factory) : IDisposable
{
    private static readonly HashSet<string> RecordedCurrencies = ["EUR", "JPY", "TND"];

    private readonly ProviderFixtures fixtures = new();

    private FrankfurterClient Client => new(new HttpClient(fixtures) { BaseAddress = new Uri("https://api.frankfurter.dev/") });

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
}
