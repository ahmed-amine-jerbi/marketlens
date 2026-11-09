using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Marketlens.Api.Data;
using Marketlens.Api.History;
using Marketlens.Api.Locale;
using Marketlens.Api.MarketData;
using Marketlens.Api.Quotes;

namespace Marketlens.Api.Tests.Http;

public sealed class HistoryStatusLocaleTests(MarketlensApiFactory factory)
{
    [Theory]
    [InlineData("1D", CandleInterval.OneMinute)]
    [InlineData("1W", CandleInterval.OneHour)]
    [InlineData("1M", CandleInterval.OneHour)]
    [InlineData("3M", CandleInterval.OneDay)]
    [InlineData("1Y", CandleInterval.OneDay)]
    [InlineData("5Y", CandleInterval.OneDay)]
    public void Each_range_reads_the_finest_interval_that_covers_it(string rangeText, CandleInterval expectedInterval)
    {
        Assert.True(HistoryQuery.TryParseRange(rangeText, out var range));

        Assert.Equal(expectedInterval, HistoryQuery.Plan(range).Interval);
    }

    [Fact]
    public void DownSample_keeps_at_most_the_limit_in_time_order_and_always_the_newest_point()
    {
        var start = new DateTime(2021, 11, 8, 0, 0, 0, DateTimeKind.Utc);
        var points = Enumerable.Range(0, 1826).Select(day => new HistoryPoint(start.AddDays(day), day)).ToList();

        var sampled = HistoryQuery.DownSample(points, HistoryQuery.MaxPoints);

        Assert.True(sampled.Count <= HistoryQuery.MaxPoints);
        Assert.Equal(points[^1], sampled[^1]);
        Assert.Equal(sampled.OrderBy(point => point.T), sampled);
    }

    [Fact]
    public async Task Stock_history_for_a_year_comes_as_daily_points_with_string_closes()
    {
        using var client = factory.CreateClient();

        var history = await EventuallyJsonAsync(client, "/api/v1/assets/stock:AAPL/history?range=1Y", HasPoints);

        Assert.Equal("1d", history.GetProperty("interval").GetString());
        Assert.Equal("fiat:USD", history.GetProperty("in").GetString());
        var points = history.GetProperty("points");
        Assert.InRange(points.GetArrayLength(), 200, 270);
        Assert.Equal(JsonValueKind.String, points[0].GetProperty("close").ValueKind);
    }

    [Fact]
    public async Task History_in_another_currency_converts_each_point_with_that_days_rate()
    {
        using var client = factory.CreateClient();

        var history = await EventuallyJsonAsync(client, "/api/v1/assets/stock:AAPL/history?range=1Y&in=fiat:EUR", HasPoints);

        // The recorded euro rates start on 2026-10-01; earlier points have no euro price to use.
        var times = history.GetProperty("points").EnumerateArray().Select(point => point.GetProperty("t").GetDateTime()).ToList();
        Assert.NotEmpty(times);
        Assert.All(times, time => Assert.True(time >= new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task Fiat_has_daily_rates_only_so_a_one_day_chart_uses_daily_points()
    {
        using var client = factory.CreateClient();

        var history = await GetJsonAsync(client, "/api/v1/assets/fiat:EUR/history?range=1D");

        Assert.Equal("1d", history.GetProperty("interval").GetString());
    }

    [Theory]
    [InlineData("1D", "fiat:USD")]
    [InlineData("1M", "fiat:USD")]
    [InlineData("1M", "crypto:BTC")]
    public async Task Tether_has_daily_rates_only_so_short_charts_show_its_daily_points(string range, string displayIn)
    {
        using var client = factory.CreateClient();

        var history = await EventuallyJsonAsync(client, $"/api/v1/assets/crypto:USDT/history?range={range}&in={displayIn}", HasPoints);

        Assert.Equal("1d", history.GetProperty("interval").GetString());
    }

    [Fact]
    public async Task Tether_changes_against_its_previous_daily_rate_while_other_coins_change_over_24_hours()
    {
        using var client = factory.CreateClient();

        var quotes = await EventuallyJsonAsync(client, "/api/v1/quotes?keys=crypto:USDT", answer => answer.GetArrayLength() == 1);

        Assert.Equal("previous-close", quotes[0].GetProperty("changeBasis").GetString());
        Assert.Equal("daily", quotes[0].GetProperty("freshness").GetString());
        Assert.Equal(ChangeBasis.Rolling24h, QuoteReader.BasisFor(AssetKey.Parse("crypto:BTC")));
    }

    [Fact]
    public async Task Conversion_history_is_the_first_asset_shown_in_the_second()
    {
        using var client = factory.CreateClient();

        var history = await EventuallyJsonAsync(client, "/api/v1/conversions/history?from=fiat:EUR&to=fiat:TND&range=5Y", HasPoints);

        Assert.Equal("fiat:TND", history.GetProperty("in").GetString());
        Assert.NotEqual(0, history.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task An_unknown_range_is_refused()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/assets/stock:AAPL/history?range=10Y", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Status_reports_each_source_and_that_stocks_are_sample_data()
    {
        using var client = factory.CreateClient();

        var status = await EventuallyJsonAsync(client, "/api/v1/status", status =>
            status.GetProperty("sources").EnumerateArray().Any(source => source.GetProperty("source").GetString() == "sample"
                && source.GetProperty("quoteCount").GetInt32() == 50));

        Assert.Equal("sample", status.GetProperty("stockData").GetString());
        var sample = status.GetProperty("sources").EnumerateArray().Single(source => source.GetProperty("source").GetString() == "sample");
        Assert.Equal(50, sample.GetProperty("quoteCount").GetInt32());
    }

    [Theory]
    [InlineData("fr-TN,fr;q=0.9,en;q=0.8", "fr", "fiat:TND", "browser-language")]
    [InlineData("de-CH", "de", "fiat:CHF", "browser-language")]
    [InlineData("en-US;q=0.5,de-DE", "de", "fiat:EUR", "browser-language")]
    [InlineData("es-ES,es;q=0.9", "en", "fiat:EUR", "browser-language")]
    [InlineData("ja", "en", "fiat:USD", "default")]
    [InlineData("", "en", "fiat:USD", "default")]
    [InlineData(null, "en", "fiat:USD", "default")]
    public void Suggest_picks_the_first_supported_language_and_the_first_known_regions_currency(
        string? acceptLanguage, string language, string displayCurrency, string reason)
    {
        var suggestion = LocaleSuggester.Suggest(acceptLanguage).Value!;

        Assert.Equal(new LocaleSuggestion(language, displayCurrency, reason), suggestion);
    }

    [Fact]
    public async Task Locale_suggestion_reads_the_browsers_accept_language_header()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-TN");

        var suggestion = await GetJsonAsync(client, "/api/v1/locale-suggestion");

        Assert.Equal("fr", suggestion.GetProperty("language").GetString());
        Assert.Equal("fiat:TND", suggestion.GetProperty("displayCurrency").GetString());
    }

    private static bool HasPoints(JsonElement history) => history.GetProperty("points").GetArrayLength() > 0;

    /// <summary>Polls a path until its answer satisfies <paramref name="isReady"/>: workers fill data in the background.</summary>
    private static async Task<JsonElement> EventuallyJsonAsync(HttpClient client, string path, Func<JsonElement, bool> isReady)
    {
        var answer = await GetJsonAsync(client, path);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!isReady(answer) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            answer = await GetJsonAsync(client, path);
        }

        return answer;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }
}
