using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using Marketlens.Api.Http;
using Marketlens.Api.MarketData;

namespace Marketlens.Api.Forecasts;

public sealed class ForecastOptions
{
    public const string Section = "Forecasts";

    /// <summary>From <c>FORECASTER_BASE_URL</c>; the forecaster is reachable only on the internal network.</summary>
    public Uri BaseUrl { get; set; } = new("http://localhost:8001/");

    /// <summary>Calendar days ahead; crypto forecasts every day, the other classes their business days.</summary>
    public int HorizonDays { get; set; } = 7;

    /// <summary>Weeks of rolling backtest windows, and of live evaluations in the track record.</summary>
    public int BacktestWeeks { get; set; } = 8;

    /// <summary>Days of daily closes sent to the forecaster; an asset with less history has no forecast.</summary>
    public int HistoryDays { get; set; } = 730;

    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>One asset's backtest and forecast; the slowest take a few seconds.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Forecasts and their evaluations older than this are deleted.</summary>
    public int RetentionDays { get; set; } = 90;
}

/// <summary>The forecaster's calendar for a series.</summary>
public enum ForecastFrequency
{
    /// <summary>Every calendar day (crypto).</summary>
    [JsonStringEnumMemberName("D")]
    Daily,

    /// <summary>Weekdays only (stocks, ETFs, fiat).</summary>
    [JsonStringEnumMemberName("B")]
    BusinessDays,
}

public sealed record ForecasterClose(DateOnly Date, decimal Close);

public sealed record ForecasterRequest(
    AssetKey AssetKey,
    IReadOnlyList<ForecasterClose> Closes,
    ForecastFrequency Freq,
    int HorizonSteps,
    int BacktestWeeks);

public sealed record ForecasterPoint(DateOnly Date, decimal Median, decimal Low80, decimal High80, decimal Low95, decimal High95);

public sealed record ForecasterBacktest(int WindowWeeks, decimal Coverage80Pct, decimal MeanErrorPct, decimal BaselineErrorPct, bool BeatsBaseline);

public sealed record ForecasterResponse(string Model, IReadOnlyList<ForecasterPoint> Points, ForecasterBacktest Backtest);

/// <summary>The forecaster refused a series (too short, malformed); retrying the same series won't help.</summary>
public sealed class ForecasterRefusedException(AssetKey asset, string detail)
    : Exception($"The forecaster refused the series for {asset}: {detail}")
{
    public AssetKey Asset { get; } = asset;
}

/// <summary>
/// Adapter for the internal forecaster (<c>POST /v1/forecasts</c>, ADR 0005). Speaks the API's JSON
/// conventions (decimals as strings, D53) and checks every answer against the request before
/// anything is stored.
/// </summary>
public sealed class ForecasterClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = CreateJsonOptions();

    public async Task<ForecasterResponse> ForecastAsync(ForecasterRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await http.PostAsJsonAsync("v1/forecasts", request, Json, cancellationToken);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            throw new ForecasterRefusedException(request.AssetKey, await response.Content.ReadAsStringAsync(cancellationToken));
        }

        response.EnsureSuccessStatusCode();
        var forecast = await response.Content.ReadFromJsonAsync<ForecasterResponse>(Json, cancellationToken)
            ?? throw new JsonException($"The forecaster returned an empty body for {request.AssetKey}.");
        EnsureAnswers(request, forecast);
        return forecast;
    }

    /// <summary>
    /// Throws unless the response has one point per step, dated after the last close in ascending
    /// order, with positive ranges nested around the median.
    /// </summary>
    public static void EnsureAnswers(ForecasterRequest request, ForecasterResponse forecast)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(forecast);
        if (forecast.Points.Count != request.HorizonSteps)
        {
            throw new InvalidDataException($"The forecaster returned {forecast.Points.Count} points for {request.AssetKey}; {request.HorizonSteps} were asked for.");
        }

        var previousDate = request.Closes[^1].Date;
        foreach (var point in forecast.Points)
        {
            if (point.Date <= previousDate)
            {
                throw new InvalidDataException($"The forecaster's point {point.Date} for {request.AssetKey} is not after {previousDate}.");
            }

            if (!(point.Low95 > 0 && point.Low95 <= point.Low80 && point.Low80 <= point.Median && point.Median <= point.High80 && point.High80 <= point.High95))
            {
                throw new InvalidDataException($"The forecaster's ranges for {request.AssetKey} on {point.Date} are not positive and nested.");
            }

            previousDate = point.Date;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ApiConventions.ConfigureJson(options);
        return options;
    }
}
