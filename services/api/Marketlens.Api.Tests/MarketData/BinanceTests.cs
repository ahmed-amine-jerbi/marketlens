using System.Globalization;
using System.Text;
using System.Text.Json;

using Marketlens.Api.Data;
using Marketlens.Api.MarketData;

namespace Marketlens.Api.Tests.MarketData;

public sealed class BinanceTests : IDisposable
{
    private static readonly AssetKey Bitcoin = AssetKey.Parse("crypto:BTC");
    private static readonly AssetKey Ether = AssetKey.Parse("crypto:ETH");
    private static readonly Dictionary<string, AssetKey> ListedCoins = new() { ["BTCUSDT"] = Bitcoin, ["ETHUSDT"] = Ether };

    private readonly ProviderFixtures fixtures = new();

    private BinanceMarketDataClient Client => new(new HttpClient(fixtures) { BaseAddress = new Uri("https://data-api.binance.vision/") });

    public void Dispose() => fixtures.Dispose();

    [Fact]
    public void ParseFrame_reads_a_recorded_mini_ticker_as_a_live_tick_with_its_24_hour_reference()
    {
        var recordedFrames = JsonDocument.Parse(ProviderFixtures.Read("binance-stream-miniticker.json")).RootElement;
        var firstFrame = Encoding.UTF8.GetBytes(recordedFrames[0].GetRawText());

        var tick = BinanceLiveFeed.ParseFrame(firstFrame, ListedCoins);

        Assert.NotNull(tick);
        Assert.Equal(Bitcoin, tick.Asset);
        Assert.Equal(76028.01m, tick.PriceUsd);
        Assert.Equal(75873.48m, tick.ReferencePriceUsd);
        Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(1794150729014), tick.ObservedAtUtc);
        Assert.Equal(Freshness.Live, tick.Freshness);
    }

    [Fact]
    public void ParseFrame_reads_every_recorded_frame()
    {
        var recordedFrames = JsonDocument.Parse(ProviderFixtures.Read("binance-stream-miniticker.json")).RootElement.EnumerateArray();

        var ticks = recordedFrames.Select(frame => BinanceLiveFeed.ParseFrame(Encoding.UTF8.GetBytes(frame.GetRawText()), ListedCoins)).ToList();

        Assert.All(ticks, Assert.NotNull);
    }

    [Theory]
    [InlineData("""{"stream":"solusdt@miniTicker","data":{"e":"24hrMiniTicker","E":1,"s":"SOLUSDT","c":"150.1","o":"149.0"}}""")]
    [InlineData("""{"result":null,"id":1}""")]
    [InlineData("""{"stream":"btcusdt@miniTicker","data":{"e":"24hrMiniTicker","E":1,"s":"BTCUSDT","c":"not a number","o":"1"}}""")]
    [InlineData("""{"stream":"btcusdt@miniTicker","data":{"e":"24hrMiniTicker","E":1,"s":"BTCUSDT","c":"0","o":"1"}}""")]
    [InlineData("""not json""")]
    [InlineData("""{"stream":"btcusdt@miniTicker","data":{"e":"24hrMiniTicker","E":"later","s":"BTCUSDT","c":"1","o":"1"}}""")]
    [InlineData("""{"stream":"btcusdt@miniTicker","data":{"e":"24hrMiniTicker","E":1,"s":"BTCUSDT","c":1.5,"o":"1"}}""")]
    [InlineData("""{"stream":"btcusdt@miniTicker","data":[1,2]}""")]
    public void ParseFrame_ignores_frames_that_are_not_a_valid_ticker_for_a_listed_coin(string frame)
    {
        var tick = BinanceLiveFeed.ParseFrame(Encoding.UTF8.GetBytes(frame), ListedCoins);

        Assert.Null(tick);
    }

    [Fact]
    public async Task FetchCandlesAsync_reads_recorded_klines_as_usd_candles()
    {
        var from = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);

        var candles = await Client.FetchCandlesAsync(Bitcoin, "BTCUSDT", CandleInterval.OneDay, from, from.AddDays(3), TestContext.Current.CancellationToken);

        Assert.Equal(3, candles.Count);
        var first = candles[0];
        Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(1793923200000), first.BucketStartUtc);
        Assert.Equal(76088.01m, first.OpenUsd);
        Assert.Equal(76416.24m, first.HighUsd);
        Assert.Equal(74888.00m, first.LowUsd);
        Assert.Equal(75844.00m, first.CloseUsd);
        Assert.Equal(17207.67786m, first.Volume);
        Assert.Equal(BinanceMarketDataClient.SourceName, first.Source);
    }

    [Fact]
    public async Task FetchCandlesAsync_pages_past_the_thousand_kline_limit()
    {
        var from = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        fixtures.Respond("/api/v3/klines", request => KlinePage(request, maxCount: BinanceMarketDataClient.MaxKlinesPerRequest, endUtc: from.AddMinutes(1500)));

        var candles = await Client.FetchCandlesAsync(Bitcoin, "BTCUSDT", CandleInterval.OneMinute, from, from.AddMinutes(1500), TestContext.Current.CancellationToken);

        Assert.Equal(1500, candles.Count);
        Assert.Equal(2, fixtures.Requests.Count);
        Assert.Equal(candles.Select(candle => candle.BucketStartUtc).Distinct().Count(), candles.Count);
        Assert.Contains($"startTime={ToUnixMs(from.AddMinutes(1000))}", fixtures.Requests[1].Query, StringComparison.Ordinal);
    }

    /// <summary>One-minute klines from the request's startTime, as Binance would page them.</summary>
    private static string KlinePage(Uri request, int maxCount, DateTime endUtc)
    {
        var query = request.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=')).ToDictionary(pair => pair[0], pair => pair[1]);
        var startUtc = DateTime.UnixEpoch.AddMilliseconds(long.Parse(query["startTime"], CultureInfo.InvariantCulture));
        var count = Math.Min(maxCount, (int)(endUtc - startUtc).TotalMinutes);

        var klines = Enumerable.Range(0, count).Select(minute =>
        {
            var openTime = ToUnixMs(startUtc.AddMinutes(minute));
            return $"""[{openTime},"100.0","101.0","99.0","100.5","1.5",{openTime + 59_999},"150.0",10,"0.7","70.0","0"]""";
        });
        return "[" + string.Join(',', klines) + "]";
    }

    private static long ToUnixMs(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalMilliseconds;
}
