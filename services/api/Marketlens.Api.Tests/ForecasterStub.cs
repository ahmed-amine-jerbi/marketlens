using System.Net;
using System.Net.Http.Json;

using Marketlens.Api.Forecasts;
using Marketlens.Api.MarketData;

namespace Marketlens.Api.Tests;

/// <summary>
/// Stands in for the forecaster in the test host: answers every request with a well-formed forecast
/// (one point per step after the last close, ranges around it), so the scheduler and the endpoint
/// run end to end without Python. The real contract is covered by ForecasterContractTests.
/// </summary>
public sealed class ForecasterStub : HttpMessageHandler
{
    /// <summary>The asset whose backtest never beats "no change".</summary>
    public static readonly AssetKey NotBetterAsset = AssetKey.Parse("stock:MSFT");

    public const string ChosenModel = "AutoETS";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var forecastRequest = await request.Content!.ReadFromJsonAsync<ForecasterRequest>(ForecasterClient.Json, cancellationToken)
            ?? throw new InvalidOperationException("The forecaster stub got an empty request.");

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(Answer(forecastRequest), options: ForecasterClient.Json),
            RequestMessage = request,
        };
    }

    private static ForecasterResponse Answer(ForecasterRequest request)
    {
        var lastClose = request.Closes[^1];
        var beatsBaseline = request.AssetKey != NotBetterAsset;
        var points = FutureDates(lastClose.Date, request.Freq)
            .Take(request.HorizonSteps)
            .Select(date => new ForecasterPoint(date, lastClose.Close, lastClose.Close * 0.95m, lastClose.Close * 1.05m, lastClose.Close * 0.9m, lastClose.Close * 1.1m))
            .ToList();

        return new ForecasterResponse(
            beatsBaseline ? ChosenModel : "Naive",
            points,
            new ForecasterBacktest(request.BacktestWeeks, 81.25m, beatsBaseline ? 1.5m : 2m, 2m, beatsBaseline));
    }

    private static IEnumerable<DateOnly> FutureDates(DateOnly lastClose, ForecastFrequency frequency)
    {
        for (var date = lastClose.AddDays(1); ; date = date.AddDays(1))
        {
            if (frequency == ForecastFrequency.Daily || date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                yield return date;
            }
        }
    }
}
