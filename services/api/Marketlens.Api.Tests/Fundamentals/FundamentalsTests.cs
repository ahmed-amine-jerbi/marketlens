using System.Globalization;
using System.Net;
using System.Text.Json;

using Marketlens.Api.Data;
using Marketlens.Api.Fundamentals;
using Marketlens.Api.MarketData;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Marketlens.Api.Tests.Fundamentals;

/// <summary>
/// The refresher, built over recorded Finnhub and SEC fixtures with a test key (the shared host has
/// none), fills the tables; the endpoint serves them converted into the display currency.
/// </summary>
public sealed class FundamentalsTests(MarketlensApiFactory factory) : IDisposable
{
    private const string AppleCik = "0000320193";
    private const string IntelCik = "0000050863";

    private readonly ProviderFixtures fixtures = new();

    public void Dispose() => fixtures.Dispose();

    [Theory]
    [InlineData(AssetClass.Etf, false, true, FundamentalsStatus.NotCovered)]
    [InlineData(AssetClass.Crypto, true, true, FundamentalsStatus.NotCovered)]
    [InlineData(AssetClass.Stock, true, false, FundamentalsStatus.Available)]
    [InlineData(AssetClass.Stock, false, true, FundamentalsStatus.Pending)]
    [InlineData(AssetClass.Stock, false, false, FundamentalsStatus.NoProviderKey)]
    public void Status_says_whether_figures_exist_could_be_fetched_or_dont_apply(AssetClass assetClass, bool hasStoredFigures, bool hasProviderKey, FundamentalsStatus expected)
    {
        Assert.Equal(expected, FundamentalsEndpoints.StatusOf(assetClass, hasStoredFigures, hasProviderKey));
    }

    [Fact]
    public void Market_cap_moves_with_the_price_since_it_was_fetched()
    {
        Assert.Equal(5_500_000_000_000m, FundamentalsEndpoints.CurrentMarketCapUsd(5_000_000_000_000m, 200m, 220m));
        Assert.Equal(5_000_000_000_000m, FundamentalsEndpoints.CurrentMarketCapUsd(5_000_000_000_000m, null, 220m));
        Assert.Null(FundamentalsEndpoints.CurrentMarketCapUsd(null, 200m, 220m));
    }

    [Fact]
    public void Shares_are_kept_only_when_they_agree_with_the_market_cap()
    {
        // Apple: 14,594.18 M shares at 354.76 make Finnhub's 5,177,380 M market cap.
        Assert.Equal(14_594_180_000m, FundamentalsRefresher.ConsistentShares(14_594_180_000m, 5_177_380_424_341m, 354.76m, tolerancePct: 5));
        // Berkshire: 1.43 M shares (another class) against a 1.26 T market cap.
        Assert.Null(FundamentalsRefresher.ConsistentShares(1_430_000m, 1_259_396_050_988m, 593.865m, tolerancePct: 5));
        Assert.Null(FundamentalsRefresher.ConsistentShares(14_594_180_000m, 5_177_380_424_341m, priceUsd: null, tolerancePct: 5));
    }

    [Fact]
    public async Task A_refreshed_stock_serves_its_figures_years_and_analyst_counts()
    {
        await RefreshFromFixturesAsync();
        using var client = factory.CreateClient();

        var apple = await GetJsonAsync(client, "/api/v1/assets/stock:AAPL/fundamentals");

        Assert.Equal("available", apple.GetProperty("status").GetString());
        Assert.Equal("Technology", apple.GetProperty("profile").GetProperty("industry").GetString());
        Assert.Equal("https://www.apple.com/", apple.GetProperty("profile").GetProperty("websiteUrl").GetString());
        Assert.Equal(39.1706m, DecimalOf(apple.GetProperty("figures"), "peRatio"));
        Assert.True(DecimalOf(apple.GetProperty("figures"), "marketCap") > 1_000_000_000_000m);
        Assert.Equal("2027-01-27T00:00:00Z", apple.GetProperty("figures").GetProperty("nextEarningsDateUtc").GetString());

        var years = apple.GetProperty("financialYears");
        Assert.Equal(5, years.GetArrayLength());
        Assert.Equal(478_834_000_000m, DecimalOf(years[0], "revenue"));
        Assert.Equal(132_175_000_000m, DecimalOf(years[0], "netIncome"));
        Assert.Equal("2026-09-26T00:00:00Z", years[0].GetProperty("periodEndUtc").GetString());

        var trends = apple.GetProperty("analystTrends");
        Assert.Equal(4, trends.GetArrayLength());
        Assert.Equal(11, trends[0].GetProperty("strongBuy").GetInt32());
        Assert.Equal(["finnhub", "sec-edgar"], apple.GetProperty("sources").EnumerateArray().Select(source => source.GetString()));
        Assert.Equal("static2.finnhub.io", new Uri(Listing("stock:AAPL").LogoUrl ?? "https://missing.invalid/").Host);
    }

    [Fact]
    public async Task A_loss_converts_into_the_display_currency_as_a_negative_amount()
    {
        await RefreshFromFixturesAsync();
        using var client = factory.CreateClient();

        var inUsd = await GetJsonAsync(client, "/api/v1/assets/stock:INTC/fundamentals");
        var inEuros = await GetJsonAsync(client, "/api/v1/assets/stock:INTC/fundamentals?in=fiat:EUR");

        Assert.Equal(-267_000_000m, DecimalOf(inUsd.GetProperty("financialYears")[0], "netIncome"));
        var lossInEuros = DecimalOf(inEuros.GetProperty("financialYears")[0], "netIncome");
        Assert.True(lossInEuros is < 0 and not -267_000_000m);
        Assert.Null(DecimalOrNull(inUsd.GetProperty("figures"), "peRatio"));
        Assert.Equal(-2.3913m, DecimalOf(inUsd.GetProperty("figures"), "epsTtm"));
    }

    [Fact]
    public async Task Exact_company_totals_survive_the_round_trip_through_the_database()
    {
        var listing = Listing("stock:KO");
        var periodEnd = new DateOnly(1999, 12, 31);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
            db.AssetFinancialYears.Add(new AssetFinancialYear
            {
                AssetId = listing.Id,
                PeriodStartDate = new DateOnly(1999, 1, 1),
                PeriodEndDate = periodEnd,
                RevenueUsd = 478_834_000_000.1234m,
                NetIncomeUsd = -14_800_000_000m,
                FiledDate = new DateOnly(2000, 3, 1),
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readScope = factory.Services.CreateAsyncScope();
        var stored = await readScope.ServiceProvider.GetRequiredService<MarketlensDbContext>().AssetFinancialYears
            .AsNoTracking()
            .SingleAsync(year => year.AssetId == listing.Id && year.PeriodEndDate == periodEnd, TestContext.Current.CancellationToken);
        Assert.Equal(478_834_000_000.1234m, stored.RevenueUsd);
        Assert.Equal(-14_800_000_000m, stored.NetIncomeUsd);
    }

    [Theory]
    [InlineData("etf:SPY")]
    [InlineData("crypto:BTC")]
    public async Task Funds_and_coins_are_not_covered(string key)
    {
        using var client = factory.CreateClient();

        var answer = await GetJsonAsync(client, $"/api/v1/assets/{key}/fundamentals");

        Assert.Equal("not-covered", answer.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("figures").ValueKind);
        Assert.Equal(0, answer.GetProperty("financialYears").GetArrayLength());
    }

    [Theory]
    [InlineData("/api/v1/assets/AAPL/fundamentals", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/assets/stock:NOTASTOCK/fundamentals", HttpStatusCode.NotFound)]
    [InlineData("/api/v1/assets/stock:AAPL/fundamentals?in=fiat:XXX", HttpStatusCode.NotFound)]
    public async Task Bad_requests_are_problem_details(string path, HttpStatusCode expected)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    // Answers Finnhub and SEC from the recordings: Apple and Intel in full, every other stock with
    // Finnhub's "unknown symbol" answers and no filings, so the run is quick and touches no network.
    private async Task RefreshFromFixturesAsync()
    {
        fixtures.Respond("/api/v1/stock/profile2", uri => SymbolOf(uri) switch
        {
            "AAPL" => ProviderFixtures.Read("finnhub-profile2-aapl.json"),
            _ => "{}",
        });
        fixtures.Respond("/api/v1/stock/metric", uri => SymbolOf(uri) switch
        {
            "AAPL" => ProviderFixtures.Read("finnhub-metric-aapl.json"),
            "INTC" => ProviderFixtures.Read("finnhub-metric-intc.json"),
            _ => """{"metric":{}}""",
        });
        fixtures.Respond("/api/v1/stock/recommendation", uri => SymbolOf(uri) == "AAPL" ? ProviderFixtures.Read("finnhub-recommendation-aapl.json") : "[]");
        fixtures.Respond("/api/v1/calendar/earnings", uri => SymbolOf(uri) == "AAPL" ? ProviderFixtures.Read("finnhub-earnings-calendar-aapl.json") : """{"earningsCalendar":[]}""");
        foreach (var listing in factory.Services.GetRequiredService<AssetUniverse>().Stocks)
        {
            var fixture = listing.SecCik switch
            {
                AppleCik => ProviderFixtures.Read("sec-companyfacts-aapl.json"),
                IntelCik => ProviderFixtures.Read("sec-companyfacts-intc.json"),
                _ => """{"facts":{}}""",
            };
            fixtures.Respond($"/api/xbrl/companyfacts/CIK{listing.SecCik}.json", _ => fixture);
        }

        var finnhub = new FinnhubClient(new HttpClient(fixtures, disposeHandler: false) { BaseAddress = new Uri("https://finnhub.io/api/v1/") });
        var secEdgar = new SecEdgarClient(new HttpClient(fixtures, disposeHandler: false) { BaseAddress = new Uri("https://data.sec.gov/") });
        var refresher = ActivatorUtilities.CreateInstance<FundamentalsRefresher>(
            factory.Services,
            finnhub,
            secEdgar,
            Options.Create(new FundamentalsOptions { RequestSpacing = TimeSpan.Zero }),
            Options.Create(new FinnhubOptions { ApiKey = "test-key" }),
            Options.Create(new SecEdgarOptions { UserAgent = "Marketlens tests contact@example.invalid" }));

        await refresher.RunOnceAsync(TestContext.Current.CancellationToken);
    }

    private static string SymbolOf(Uri uri) =>
        System.Web.HttpUtility.ParseQueryString(uri.Query)["symbol"] ?? string.Empty;

    private CatalogEntry Listing(string key)
    {
        Assert.True(factory.Services.GetRequiredService<AssetCatalog>().TryGet(AssetKey.Parse(key), out var listing));
        return listing;
    }

    private static decimal DecimalOf(JsonElement parent, string property) =>
        decimal.Parse(parent.GetProperty(property).GetString() ?? throw new InvalidDataException($"{property} is null."), CultureInfo.InvariantCulture);

    private static decimal? DecimalOrNull(JsonElement parent, string property) =>
        parent.GetProperty(property).ValueKind == JsonValueKind.Null ? null : DecimalOf(parent, property);

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }
}
