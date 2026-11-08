using System.Text;
using System.Text.Json;

using Marketlens.Api.MarketData;

namespace Marketlens.Api.Tests.MarketData;

/// <summary>
/// Fixtures for these adapters follow the providers' documented response shapes; they were not
/// recorded live because no API keys existed when they were written (D44).
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
        Assert.Equal(231.4m, quote.PriceUsd);
        Assert.Equal(230.02m, quote.PreviousCloseUsd);
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1794168000), quote.TradedAtUtc);
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
