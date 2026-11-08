using System.Text.Json;

using Marketlens.Api.Data;
using Marketlens.Api.MarketData;

using Microsoft.Extensions.DependencyInjection;

namespace Marketlens.Api.Tests.MarketData;

/// <summary>The Twelve Data fixture follows the documented response shape; not recorded live (D44).</summary>
public sealed class TwelveDataAndSampleTests(MarketlensApiFactory factory)
{
    private static readonly AssetKey Apple = AssetKey.Parse("stock:AAPL");

    [Fact]
    public void ParseDailySeries_reads_daily_bars_as_usd_candles()
    {
        using var series = JsonDocument.Parse(ProviderFixtures.Read("twelvedata-time-series-aapl.json"));

        var candles = TwelveDataClient.ParseDailySeries(Apple, series.RootElement);

        Assert.Equal(2, candles.Count);
        var newest = candles[0];
        Assert.Equal(new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc), newest.BucketStartUtc);
        Assert.Equal(CandleInterval.OneDay, newest.Interval);
        Assert.Equal(229.8m, newest.OpenUsd);
        Assert.Equal(231.4m, newest.CloseUsd);
        Assert.Equal(41234567m, newest.Volume);
        Assert.Equal(TwelveDataClient.SourceName, newest.Source);
    }

    [Fact]
    public void ParseDailySeries_turns_an_in_body_rate_limit_into_a_retry_after_error()
    {
        using var limited = JsonDocument.Parse("""{"code":429,"message":"You have run out of API credits for the current minute.","status":"error"}""");

        var error = Assert.Throws<ProviderRateLimitedException>(() => TwelveDataClient.ParseDailySeries(Apple, limited.RootElement));

        Assert.Equal(TimeSpan.FromMinutes(1), error.RetryAfter);
    }

    [Fact]
    public void ParseDailySeries_reports_other_provider_errors_with_their_message()
    {
        using var refused = JsonDocument.Parse("""{"code":404,"message":"symbol not found","status":"error"}""");

        var error = Assert.Throws<HttpRequestException>(() => TwelveDataClient.ParseDailySeries(Apple, refused.RootElement));

        Assert.Contains("symbol not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sample_history_is_the_same_on_every_run_and_skips_weekends()
    {
        var lastDay = new DateOnly(2026, 11, 6);

        var firstRun = SampleStockFeed.DailyCloses("AAPL", lastDay);
        var secondRun = SampleStockFeed.DailyCloses("AAPL", lastDay);

        Assert.Equal(firstRun, secondRun);
        Assert.All(firstRun, close => Assert.True(close.CloseUsd > 0));
        Assert.DoesNotContain(firstRun, close => close.Day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.NotEqual(firstRun, SampleStockFeed.DailyCloses("MSFT", lastDay));
    }

    [Fact]
    public async Task Without_a_finnhub_key_stocks_are_priced_from_labelled_sample_data()
    {
        var quoteBook = factory.Services.GetRequiredService<QuoteBook>();

        var isPriced = await Eventually.IsTrueAsync(() => quoteBook.TryGet(Apple, out _));

        Assert.True(isPriced);
        Assert.True(quoteBook.TryGet(Apple, out var quote));
        Assert.Equal(SampleStockFeed.SourceName, quote.Source);
        Assert.NotNull(quote.ReferencePriceUsd);
    }
}
