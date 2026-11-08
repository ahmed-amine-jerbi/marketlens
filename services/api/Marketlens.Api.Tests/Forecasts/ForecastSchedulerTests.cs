using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Marketlens.Api.Data;
using Marketlens.Api.Forecasts;
using Marketlens.Api.MarketData;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Marketlens.Api.Tests.Forecasts;

/// <summary>
/// Forecasts end to end in the test host: the scheduler forecasts the sample stocks (five years of
/// history) through <see cref="ForecasterStub"/> at startup, the endpoint serves them, and the
/// evaluator turns past forecast days into a live track record.
/// </summary>
public sealed class ForecastSchedulerTests(MarketlensApiFactory factory)
{
    [Theory]
    [InlineData(AssetClass.Crypto, ForecastFrequency.Daily)]
    [InlineData(AssetClass.Stock, ForecastFrequency.BusinessDays)]
    [InlineData(AssetClass.Etf, ForecastFrequency.BusinessDays)]
    [InlineData(AssetClass.Fiat, ForecastFrequency.BusinessDays)]
    public void Crypto_is_forecast_every_day_and_the_other_classes_on_business_days(AssetClass assetClass, ForecastFrequency expected)
    {
        Assert.Equal(expected, ForecastScheduler.FrequencyOf(assetClass));
    }

    [Theory]
    [InlineData(ForecastFrequency.Daily, "2026-11-06", 7)]
    [InlineData(ForecastFrequency.BusinessDays, "2026-11-06", 5)]
    [InlineData(ForecastFrequency.BusinessDays, "2026-11-04", 5)]
    public void Seven_calendar_days_ahead_are_seven_daily_steps_or_five_business_days(ForecastFrequency frequency, string seriesEnd, int expectedSteps)
    {
        Assert.Equal(expectedSteps, ForecastScheduler.HorizonSteps(frequency, DateOnly.Parse(seriesEnd, CultureInfo.InvariantCulture), horizonDays: 7));
    }

    [Fact]
    public void Status_says_pending_or_insufficient_without_a_forecast_and_follows_the_backtest_with_one()
    {
        Assert.Equal(ForecastStatus.Pending, ForecastEndpoints.StatusOf(null, hasEnoughHistory: true));
        Assert.Equal(ForecastStatus.InsufficientHistory, ForecastEndpoints.StatusOf(null, hasEnoughHistory: false));
        Assert.Equal(ForecastStatus.Available, ForecastEndpoints.StatusOf(StoredForecast(beatsBaseline: true), hasEnoughHistory: true));
        Assert.Equal(ForecastStatus.NotBetterThanBaseline, ForecastEndpoints.StatusOf(StoredForecast(beatsBaseline: false), hasEnoughHistory: true));
    }

    [Fact]
    public void A_forecast_day_is_scored_against_its_close_and_against_no_change()
    {
        var point = Point(new DateOnly(2026, 11, 2), median: 102m, low80: 99m, high80: 101m);

        var inside = ForecastEvaluator.Evaluate(Guid.Empty, point, lastCloseUsd: 95m, actualCloseUsd: 100m);
        var outside = ForecastEvaluator.Evaluate(Guid.Empty, point, lastCloseUsd: 95m, actualCloseUsd: 98m);

        Assert.True(inside.IsInsideRange80);
        Assert.Equal(2m, inside.ErrorPct);
        Assert.Equal(5m, inside.BaselineErrorPct);
        Assert.False(outside.IsInsideRange80);
        Assert.Equal(4.0816m, outside.ErrorPct);
        Assert.Equal(3.0612m, outside.BaselineErrorPct);
    }

    [Fact]
    public async Task A_stock_with_five_years_of_history_gets_business_day_points_and_its_backtest_record()
    {
        using var client = factory.CreateClient();

        var forecast = await EventuallyAvailableAsync(client, "/api/v1/assets/stock:AAPL/forecast");

        Assert.Equal("available", forecast.GetProperty("status").GetString());
        Assert.Equal("fiat:USD", forecast.GetProperty("in").GetString());
        Assert.Equal(ForecasterStub.ChosenModel, forecast.GetProperty("model").GetString());
        Assert.Equal(7, forecast.GetProperty("horizonDays").GetInt32());
        var points = forecast.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(5, points.Count);
        Assert.All(points, point => Assert.Equal(JsonValueKind.String, point.GetProperty("median").ValueKind));
        var dates = points.Select(point => point.GetProperty("t").GetDateTime()).ToList();
        Assert.All(dates, date => Assert.True(date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)));
        Assert.Equal(dates.Order(), dates);
        var trackRecord = forecast.GetProperty("trackRecord");
        Assert.Equal("backtest", trackRecord.GetProperty("source").GetString());
        Assert.Equal(8, trackRecord.GetProperty("windowWeeks").GetInt32());
        Assert.Equal("81.25", trackRecord.GetProperty("coverage80Pct").GetString());
    }

    [Fact]
    public async Task The_range_is_shown_in_the_display_currency_at_its_latest_price()
    {
        using var client = factory.CreateClient();
        var inUsd = await EventuallyAvailableAsync(client, "/api/v1/assets/stock:AAPL/forecast");

        var inEuros = await GetJsonAsync(client, "/api/v1/assets/stock:AAPL/forecast?in=fiat:EUR");
        var euroQuote = await GetJsonAsync(client, "/api/v1/quotes?keys=fiat:EUR");

        Assert.Equal("fiat:EUR", inEuros.GetProperty("in").GetString());
        var euroInUsd = DecimalOf(euroQuote[0], "price");
        var medianUsd = DecimalOf(inUsd.GetProperty("points")[0], "median");
        var medianEuros = DecimalOf(inEuros.GetProperty("points")[0], "median");
        var expectedEuros = medianUsd / euroInUsd;
        Assert.InRange(medianEuros, expectedEuros * (1 - 1e-9m), expectedEuros * (1 + 1e-9m));
    }

    [Fact]
    public async Task An_asset_no_model_beats_gets_no_range_but_keeps_its_record()
    {
        using var client = factory.CreateClient();

        var forecast = await EventuallyJsonAsync(client, $"/api/v1/assets/{ForecasterStub.NotBetterAsset}/forecast", answer => answer.GetProperty("status").GetString() != "pending");

        Assert.Equal("not-better-than-baseline", forecast.GetProperty("status").GetString());
        Assert.Equal(0, forecast.GetProperty("points").GetArrayLength());
        Assert.Equal("2", forecast.GetProperty("trackRecord").GetProperty("baselineErrorPct").GetString());
    }

    [Fact]
    public async Task An_asset_with_a_few_days_of_history_is_insufficient()
    {
        using var client = factory.CreateClient();

        var forecast = await GetJsonAsync(client, "/api/v1/assets/crypto:BTC/forecast");

        Assert.Equal("insufficient-history", forecast.GetProperty("status").GetString());
        Assert.Equal(0, forecast.GetProperty("points").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, forecast.GetProperty("trackRecord").ValueKind);
    }

    [Theory]
    [InlineData("/api/v1/assets/BTC/forecast", HttpStatusCode.BadRequest, "key")]
    [InlineData("/api/v1/assets/stock:AAPL/forecast?in=EUR", HttpStatusCode.BadRequest, "in")]
    [InlineData("/api/v1/assets/crypto:NOTACOIN/forecast", HttpStatusCode.NotFound, null)]
    public async Task Malformed_or_unknown_assets_get_problem_details(string path, HttpStatusCode expectedStatus, string? expectedParameter)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        if (expectedParameter is not null)
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal(expectedParameter, problem.GetProperty("parameter").GetString());
        }
    }

    [Fact]
    public async Task Once_enough_past_days_are_evaluated_the_track_record_comes_from_live_results()
    {
        var nvidia = AssetKey.Parse("stock:NVDA");
        using var client = factory.CreateClient();
        await EventuallyAvailableAsync(client, $"/api/v1/assets/{nvidia}/forecast");
        var pastCloses = await ReadRecentClosesAsync(nvidia, count: 21);

        // 15 days inside their 80% range, 6 outside: 71.4286% coverage.
        await StorePastForecastAsync(nvidia, pastCloses.Select((close, index) => index < 15
            ? Point(close.Date, median: close.CloseUsd, low80: close.CloseUsd * 0.5m, high80: close.CloseUsd * 2m)
            : Point(close.Date, median: close.CloseUsd * 2.5m, low80: close.CloseUsd * 2m, high80: close.CloseUsd * 3m)));
        var evaluatedCount = await factory.Services.GetRequiredService<ForecastEvaluator>().EvaluateDueAsync(TestContext.Current.CancellationToken);

        var forecast = await GetJsonAsync(client, $"/api/v1/assets/{nvidia}/forecast");

        Assert.Equal(21, evaluatedCount);
        var trackRecord = forecast.GetProperty("trackRecord");
        Assert.Equal("live", trackRecord.GetProperty("source").GetString());
        Assert.Equal("71.4286", trackRecord.GetProperty("coverage80Pct").GetString());
        Assert.Equal(0, await factory.Services.GetRequiredService<ForecastEvaluator>().EvaluateDueAsync(TestContext.Current.CancellationToken));
    }

    private static Forecast StoredForecast(bool beatsBaseline) => new()
    {
        AssetId = Guid.Empty,
        CreatedAtUtc = DateTime.UtcNow,
        SeriesEndDate = new DateOnly(2026, 11, 6),
        LastCloseUsd = 100m,
        Model = beatsBaseline ? "AutoETS" : "Naive",
        HorizonDays = 7,
        Backtest = new ForecastBacktest { WindowWeeks = 8, Coverage80Pct = 80m, MeanErrorPct = 1m, BaselineErrorPct = 2m, BeatsBaseline = beatsBaseline },
    };

    private static ForecastPoint Point(DateOnly date, decimal median, decimal low80, decimal high80) => new()
    {
        Date = date,
        MedianUsd = median,
        Low80Usd = low80,
        High80Usd = high80,
        Low95Usd = low80 * 0.9m,
        High95Usd = high80 * 1.1m,
    };

    private async Task<List<(DateOnly Date, decimal CloseUsd)>> ReadRecentClosesAsync(AssetKey asset, int count)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var todayUtc = DateTime.UtcNow.Date;
        var closes = await db.PriceCandles
            .Where(candle => candle.Interval == CandleInterval.OneDay && candle.BucketStartUtc < todayUtc && db.Assets.Any(row => row.Id == candle.AssetId && row.Key == asset.ToString()))
            .OrderByDescending(candle => candle.BucketStartUtc)
            .Take(count)
            .Select(candle => new { candle.BucketStartUtc, candle.CloseUsd })
            .ToListAsync(TestContext.Current.CancellationToken);

        return closes.Select(close => (DateOnly.FromDateTime(close.BucketStartUtc), close.CloseUsd)).Reverse().ToList();
    }

    private async Task StorePastForecastAsync(AssetKey asset, IEnumerable<ForecastPoint> points)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var assetId = await db.Assets.Where(row => row.Key == asset.ToString()).Select(row => row.Id).SingleAsync(TestContext.Current.CancellationToken);
        var pointList = points.ToList();
        db.Forecasts.Add(new Forecast
        {
            AssetId = assetId,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-40),
            SeriesEndDate = pointList[0].Date.AddDays(-1),
            LastCloseUsd = pointList[0].MedianUsd,
            Model = "AutoETS",
            HorizonDays = 7,
            Backtest = new ForecastBacktest { WindowWeeks = 8, Coverage80Pct = 80m, MeanErrorPct = 1m, BaselineErrorPct = 2m, BeatsBaseline = true },
            Points = pointList,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static decimal DecimalOf(JsonElement element, string property) =>
        decimal.Parse(element.GetProperty(property).GetString()!, CultureInfo.InvariantCulture);

    private static Task<JsonElement> EventuallyAvailableAsync(HttpClient client, string path) =>
        EventuallyJsonAsync(client, path, answer => answer.GetProperty("status").GetString() == "available");

    /// <summary>Polls a path until its answer satisfies <paramref name="isReady"/>: the scheduler works in the background.</summary>
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
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }
}
