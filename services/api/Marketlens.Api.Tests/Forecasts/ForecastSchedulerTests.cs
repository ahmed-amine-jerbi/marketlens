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
    public void Status_says_pending_or_insufficient_without_a_forecast_and_available_with_one_whatever_its_backtest()
    {
        Assert.Equal(ForecastStatus.Pending, ForecastEndpoints.StatusOf(null, hasEnoughHistory: true));
        Assert.Equal(ForecastStatus.InsufficientHistory, ForecastEndpoints.StatusOf(null, hasEnoughHistory: false));
        Assert.Equal(ForecastStatus.Available, ForecastEndpoints.StatusOf(StoredForecast(beatsBaseline: true), hasEnoughHistory: true));
        Assert.Equal(ForecastStatus.Available, ForecastEndpoints.StatusOf(StoredForecast(beatsBaseline: false), hasEnoughHistory: true));
    }

    [Fact]
    public void A_forecast_is_current_while_one_of_its_days_is_today_or_later()
    {
        var forecast = StoredForecast(beatsBaseline: true);
        forecast.Points.Add(Point(new DateOnly(2026, 11, 9), median: 100m, low80: 95m, high80: 105m));
        forecast.Points.Add(Point(new DateOnly(2026, 11, 10), median: 100m, low80: 95m, high80: 105m));

        Assert.True(ForecastEndpoints.IsCurrent(forecast, new DateOnly(2026, 11, 10)));
        Assert.False(ForecastEndpoints.IsCurrent(forecast, new DateOnly(2026, 11, 11)));
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
        Assert.True(trackRecord.GetProperty("beatsBaseline").GetBoolean());
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
    public async Task An_asset_no_model_beats_still_gets_its_range_and_a_record_that_says_so()
    {
        using var client = factory.CreateClient();

        var forecast = await EventuallyAvailableAsync(client, $"/api/v1/assets/{ForecasterStub.NotBetterAsset}/forecast");

        Assert.Equal("Naive", forecast.GetProperty("model").GetString());
        Assert.Equal(5, forecast.GetProperty("points").GetArrayLength());
        var trackRecord = forecast.GetProperty("trackRecord");
        Assert.False(trackRecord.GetProperty("beatsBaseline").GetBoolean());
        Assert.Equal("2", trackRecord.GetProperty("baselineErrorPct").GetString());
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
    public async Task Evaluated_days_that_span_the_whole_window_make_the_track_record_live()
    {
        var amazon = AssetKey.Parse("stock:AMZN");
        using var client = factory.CreateClient();
        await EventuallyAvailableAsync(client, $"/api/v1/assets/{amazon}/forecast");
        var windowStart = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-8 * 7);
        var closesFromWindowStart = (await ReadClosesAsync(amazon, windowStart.AddDays(1))).Take(35).ToList();

        // 26 days on the median and inside the range, 9 at 2.5 times the close and outside it:
        // 74.2857% coverage, mean error (9 × 150%) / 35 = 38.5714%, far worse than no change.
        await StorePastForecastAsync(amazon, closesFromWindowStart.Select((close, index) => index < 26
            ? Point(close.Date, median: close.CloseUsd, low80: close.CloseUsd * 0.5m, high80: close.CloseUsd * 2m)
            : Point(close.Date, median: close.CloseUsd * 2.5m, low80: close.CloseUsd * 2m, high80: close.CloseUsd * 3m)));
        var evaluator = factory.Services.GetRequiredService<ForecastEvaluator>();
        await evaluator.EvaluateDueAsync(TestContext.Current.CancellationToken);

        var forecast = await GetJsonAsync(client, $"/api/v1/assets/{amazon}/forecast");

        var trackRecord = forecast.GetProperty("trackRecord");
        Assert.Equal("live", trackRecord.GetProperty("source").GetString());
        Assert.Equal(8, trackRecord.GetProperty("windowWeeks").GetInt32());
        Assert.Equal("74.2857", trackRecord.GetProperty("coverage80Pct").GetString());
        Assert.Equal("38.5714", trackRecord.GetProperty("meanErrorPct").GetString());
        Assert.False(trackRecord.GetProperty("beatsBaseline").GetBoolean());
    }

    [Fact]
    public async Task A_few_weeks_of_evaluated_days_keep_the_backtest_record_and_are_never_evaluated_twice()
    {
        var nvidia = AssetKey.Parse("stock:NVDA");
        using var client = factory.CreateClient();
        await EventuallyAvailableAsync(client, $"/api/v1/assets/{nvidia}/forecast");
        var recentCloses = (await ReadClosesAsync(nvidia, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30))).TakeLast(21);
        await StorePastForecastAsync(nvidia, recentCloses.Select(close => Point(close.Date, close.CloseUsd, close.CloseUsd * 0.5m, close.CloseUsd * 2m)));
        var evaluator = factory.Services.GetRequiredService<ForecastEvaluator>();

        var evaluatedCount = await evaluator.EvaluateDueAsync(TestContext.Current.CancellationToken);
        var forecast = await GetJsonAsync(client, $"/api/v1/assets/{nvidia}/forecast");

        Assert.Equal(21, evaluatedCount);
        Assert.Equal("backtest", forecast.GetProperty("trackRecord").GetProperty("source").GetString());
        Assert.Equal(0, await evaluator.EvaluateDueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_asset_the_forecaster_fails_on_waits_for_its_next_close_and_the_others_still_get_forecasts()
    {
        using var client = factory.CreateClient();
        var etfPage = await GetJsonAsync(client, "/api/v1/assets?class=etf&limit=200");
        var otherEtfs = etfPage.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("key").GetString()!)
            .Where(key => key != ForecasterStub.FailingAsset.ToString())
            .ToList();

        // The ETFs' history arrives together, so they become due in the same run; a failure that
        // ended the run would leave every ETF after the failing one without a forecast.
        var etfStatuses = new List<string>();
        foreach (var etf in otherEtfs)
        {
            etfStatuses.Add((await EventuallyAvailableAsync(client, $"/api/v1/assets/{etf}/forecast")).GetProperty("status").GetString()!);
        }

        var failing = await GetJsonAsync(client, $"/api/v1/assets/{ForecasterStub.FailingAsset}/forecast");

        Assert.Equal(11, otherEtfs.Count);
        Assert.All(etfStatuses, status => Assert.Equal("available", status));
        Assert.Equal("pending", failing.GetProperty("status").GetString());
        Assert.Equal(0, failing.GetProperty("points").GetArrayLength());
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

    /// <summary>The asset's complete daily closes from <paramref name="fromDate"/> to yesterday, oldest first.</summary>
    private async Task<List<(DateOnly Date, decimal CloseUsd)>> ReadClosesAsync(AssetKey asset, DateOnly fromDate)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var todayUtc = DateTime.UtcNow.Date;
        var closes = await db.PriceCandles
            .Where(candle => candle.Interval == CandleInterval.OneDay && candle.BucketStartUtc >= fromUtc && candle.BucketStartUtc < todayUtc && db.Assets.Any(row => row.Id == candle.AssetId && row.Key == asset.ToString()))
            .OrderBy(candle => candle.BucketStartUtc)
            .Select(candle => new { candle.BucketStartUtc, candle.CloseUsd })
            .ToListAsync(TestContext.Current.CancellationToken);

        return closes.Select(close => (DateOnly.FromDateTime(close.BucketStartUtc), close.CloseUsd)).ToList();
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
