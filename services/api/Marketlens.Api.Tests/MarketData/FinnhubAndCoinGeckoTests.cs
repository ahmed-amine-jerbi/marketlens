using System.Text;
using System.Text.Json;

using Marketlens.Api.MarketData;

namespace Marketlens.Api.Tests.MarketData;

/// <summary>
/// Finnhub REST fixtures (quote, profile, metrics, recommendations, earnings calendar) are trimmed
/// recordings from 2026-11-16; the trade stream and CoinGecko fixtures still follow the documented
/// shapes (D44).
/// </summary>
public sealed class FinnhubAndCoinGeckoTests : IDisposable
{
    private static readonly AssetKey Apple = AssetKey.Parse("stock:AAPL");
    private static readonly AssetKey Microsoft = AssetKey.Parse("stock:MSFT");
    private static readonly Dictionary<string, AssetKey> StreamedSymbols = new() { ["AAPL"] = Apple, ["MSFT"] = Microsoft };

    private readonly ProviderFixtures fixtures = new();

    public void Dispose() => fixtures.Dispose();

    [Fact]
    public void ParseQuote_reads_the_last_price_previous_close_and_trade_time()
    {
        using var quoteJson = JsonDocument.Parse(ProviderFixtures.Read("finnhub-quote-aapl.json"));

        var quote = FinnhubClient.ParseQuote(Apple, quoteJson.RootElement);

        Assert.NotNull(quote);
        Assert.Equal(355.24m, quote.PriceUsd);
        Assert.Equal(356.27m, quote.PreviousCloseUsd);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1794846081), quote.TradedAtUtc);
    }

    [Fact]
    public void ParseCompanyProfile_reads_the_profile_in_whole_units()
    {
        using var profileJson = JsonDocument.Parse(ProviderFixtures.Read("finnhub-profile2-aapl.json"));

        var profile = FinnhubClient.ParseCompanyProfile(profileJson.RootElement);

        Assert.NotNull(profile);
        Assert.Equal("Technology", profile.Industry);
        Assert.Equal("NASDAQ NMS - GLOBAL MARKET", profile.Exchange);
        Assert.Equal(new DateOnly(1980, 12, 12), profile.IpoDate);
        Assert.Equal(new Uri("https://www.apple.com/"), profile.WebsiteUrl);
        Assert.Equal("static2.finnhub.io", profile.LogoUrl?.Host);
        Assert.Equal(5_177_380_424_341.398m, profile.MarketCapUsd);
        Assert.Equal(14_594_180_000m, profile.SharesOutstanding);
    }

    [Theory]
    [InlineData("""{"ticker":"X","weburl":"http://example.com/"}""")]
    [InlineData("""{"ticker":"X","weburl":"javascript:alert(1)"}""")]
    [InlineData("""{"ticker":"X","weburl":"/relative"}""")]
    public void ParseCompanyProfile_keeps_only_absolute_https_websites(string json)
    {
        using var profileJson = JsonDocument.Parse(json);

        Assert.Null(FinnhubClient.ParseCompanyProfile(profileJson.RootElement)?.WebsiteUrl);
    }

    [Fact]
    public void ParseCompanyProfile_drops_a_logo_from_another_host_and_cuts_long_text()
    {
        var longIndustry = new string('x', FinnhubClient.MaxTextLength + 50);
        using var profileJson = JsonDocument.Parse($$"""{"ticker":"X","logo":"https://images.example.com/x.png","finnhubIndustry":"{{longIndustry}}"}""");

        var profile = FinnhubClient.ParseCompanyProfile(profileJson.RootElement);

        Assert.NotNull(profile);
        Assert.Null(profile.LogoUrl);
        Assert.Equal(FinnhubClient.MaxTextLength, profile.Industry?.Length);
    }

    [Fact]
    public void ParseCompanyProfile_leaves_money_out_for_a_company_reporting_in_another_currency()
    {
        using var profileJson = JsonDocument.Parse("""{"ticker":"ASML","currency":"EUR","marketCapitalization":250000,"shareOutstanding":390}""");

        var profile = FinnhubClient.ParseCompanyProfile(profileJson.RootElement);

        Assert.NotNull(profile);
        Assert.Null(profile.MarketCapUsd);
        Assert.Equal(390_000_000m, profile.SharesOutstanding);
    }

    [Fact]
    public void ParseCompanyProfile_returns_null_for_an_unknown_symbol()
    {
        using var empty = JsonDocument.Parse("{}");

        Assert.Null(FinnhubClient.ParseCompanyProfile(empty.RootElement));
    }

    [Fact]
    public void ParseBasicFinancials_reads_the_ratios_and_leaves_a_missing_pe_empty_for_a_loss()
    {
        using var apple = JsonDocument.Parse(ProviderFixtures.Read("finnhub-metric-aapl.json"));
        using var intel = JsonDocument.Parse(ProviderFixtures.Read("finnhub-metric-intc.json"));

        var appleFigures = FinnhubClient.ParseBasicFinancials(apple.RootElement);
        var intelFigures = FinnhubClient.ParseBasicFinancials(intel.RootElement);

        Assert.Equal(new BasicFinancials(39.1706m, 8.974499999999999m, 0.50534m, 360.83m, 243.42m, 1.0898671m), appleFigures);
        Assert.Null(intelFigures.PeRatio);
        Assert.Equal(-2.3913m, intelFigures.EpsTtmUsd);
    }

    [Fact]
    public void ParseRecommendationTrends_returns_the_months_newest_first()
    {
        using var trendsJson = JsonDocument.Parse(ProviderFixtures.Read("finnhub-recommendation-aapl.json"));

        var trends = FinnhubClient.ParseRecommendationTrends(trendsJson.RootElement);

        Assert.Equal(new RecommendationTrend(new DateOnly(2026, 11, 1), 11, 19, 15, 3, 1), trends[0]);
        Assert.True(trends.Count > 1);
        Assert.True(trends[0].Period > trends[1].Period);
    }

    [Fact]
    public void ParseRecommendationTrends_keeps_one_row_per_month()
    {
        using var repeated = JsonDocument.Parse("""
            [{"period":"2026-11-01","strongBuy":1,"buy":2,"hold":3,"sell":0,"strongSell":0},
             {"period":"2026-11-01","strongBuy":1,"buy":2,"hold":3,"sell":0,"strongSell":0}]
            """);

        Assert.Single(FinnhubClient.ParseRecommendationTrends(repeated.RootElement));
    }

    [Fact]
    public void ParseNextEarningsDate_reads_the_first_scheduled_date()
    {
        using var calendar = JsonDocument.Parse(ProviderFixtures.Read("finnhub-earnings-calendar-aapl.json"));

        Assert.Equal(new DateOnly(2027, 1, 27), FinnhubClient.ParseNextEarningsDate(calendar.RootElement, new DateOnly(2026, 11, 16)));
        Assert.Null(FinnhubClient.ParseNextEarningsDate(calendar.RootElement, new DateOnly(2027, 2, 1)));
    }

    [Fact]
    public void ParseQuote_treats_finnhubs_all_zero_answer_as_an_unknown_symbol()
    {
        using var unknownSymbol = JsonDocument.Parse("""{"c":0,"d":null,"dp":null,"h":0,"l":0,"o":0,"pc":0,"t":0}""");

        Assert.Null(FinnhubClient.ParseQuote(Apple, unknownSymbol.RootElement));
    }

    [Fact]
    public void ParseTradeMessage_keeps_the_last_trade_per_listed_symbol_with_exact_decimals()
    {
        var message = Encoding.UTF8.GetBytes(ProviderFixtures.Read("finnhub-ws-trades.json"));

        var ticks = FinnhubClient.ParseTradeMessage(message, StreamedSymbols).ToDictionary(tick => tick.Asset);

        Assert.Equal(2, ticks.Count);
        Assert.Equal(231.41m, ticks[Apple].PriceUsd);
        Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(1794168000456), ticks[Apple].ObservedAtUtc);
        Assert.Equal(512.07m, ticks[Microsoft].PriceUsd);
        Assert.Null(ticks[Apple].ReferencePriceUsd);
        Assert.Equal(Freshness.Live, ticks[Apple].Freshness);
    }

    [Theory]
    [InlineData("""{"type":"ping"}""")]
    [InlineData("""{"type":"trade","data":[{"s":"AAPL","p":"231.4","t":1}]}""")]
    [InlineData("""{"type":"trade","data":[{"s":"AAPL","p":231.4,"t":"soon"}]}""")]
    [InlineData("""{"type":"trade","data":[{"s":42,"p":231.4,"t":1}]}""")]
    [InlineData("""{"type":"trade","data":"none"}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""not json""")]
    public void ParseTradeMessage_ignores_messages_that_are_not_valid_trades(string message)
    {
        var ticks = FinnhubClient.ParseTradeMessage(Encoding.UTF8.GetBytes(message), StreamedSymbols);

        Assert.Empty(ticks);
    }

    [Fact]
    public void SubscribeMessage_is_finnhubs_subscribe_frame()
    {
        Assert.Equal("""{"type":"subscribe","symbol":"BRK.B"}""", FinnhubClient.SubscribeMessage("BRK.B"));
    }

    [Fact]
    public async Task FetchLogoUrlsAsync_keeps_https_logos_by_coingecko_id()
    {
        fixtures.Respond("/api/v3/coins/markets", _ => ProviderFixtures.Read("coingecko-markets.json"));
        var client = new CoinGeckoClient(new HttpClient(fixtures) { BaseAddress = new Uri("https://api.coingecko.com/api/v3/") });

        var logos = await client.FetchLogoUrlsAsync(["bitcoin", "ethereum", "insecure"], TestContext.Current.CancellationToken);

        Assert.Equal(2, logos.Count);
        Assert.StartsWith("https://", logos["bitcoin"], StringComparison.Ordinal);
        Assert.False(logos.ContainsKey("insecure"));
        Assert.Contains("ids=bitcoin,ethereum,insecure", Assert.Single(fixtures.Requests).Query, StringComparison.Ordinal);
    }
}
