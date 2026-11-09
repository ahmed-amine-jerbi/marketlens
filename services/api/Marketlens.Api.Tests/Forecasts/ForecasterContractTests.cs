using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Marketlens.Api.Forecasts;
using Marketlens.Api.MarketData;

namespace Marketlens.Api.Tests.Forecasts;

/// <summary>
/// The API's side of the forecaster contract, against the request and response the forecaster's own
/// tests use (services/forecaster/tests/contract), so a change on either side breaks a test.
/// </summary>
public sealed class ForecasterContractTests
{
    private static readonly string RecordedRequest = ProviderFixtures.Read("forecaster/forecast-request-btc.json");
    private static readonly string RecordedResponse = ProviderFixtures.Read("forecaster/forecast-response-btc.json");

    [Fact]
    public async Task The_request_the_client_sends_has_the_shape_of_the_recorded_request()
    {
        var forecaster = new StubForecaster(HttpStatusCode.OK, RecordedResponse);
        var client = new ForecasterClient(forecaster.CreateHttpClient());

        await client.ForecastAsync(BtcRequest(), TestContext.Current.CancellationToken);

        using var sent = JsonDocument.Parse(forecaster.RequestBody!);
        using var recorded = JsonDocument.Parse(RecordedRequest);
        Assert.Equal(Shape(recorded.RootElement), Shape(sent.RootElement));
        Assert.Equal("D", sent.RootElement.GetProperty("freq").GetString());
        Assert.Equal("crypto:BTC", sent.RootElement.GetProperty("assetKey").GetString());
        Assert.Equal(new Uri("http://forecaster/v1/forecasts"), forecaster.RequestUri);
    }

    [Fact]
    public async Task The_recorded_response_reads_into_exact_decimal_points_and_a_backtest()
    {
        var client = new ForecasterClient(new StubForecaster(HttpStatusCode.OK, RecordedResponse).CreateHttpClient());

        var forecast = await client.ForecastAsync(BtcRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("AutoTheta", forecast.Model);
        Assert.Equal(7, forecast.Points.Count);
        Assert.Equal(new ForecasterPoint(new DateOnly(2026, 11, 8), 75848.96512m, 73998.4275m, 77745.7806m, 72477.25261m, 79377.53298m), forecast.Points[0]);
        Assert.Equal(new DateOnly(2026, 11, 14), forecast.Points[^1].Date);
        Assert.Equal(new ForecasterBacktest(8, 96.43m, 1.84m, 1.86m, BeatsBaseline: true), forecast.Backtest);
    }

    [Fact]
    public async Task A_response_with_fewer_points_than_steps_is_rejected()
    {
        var sixPoints = JsonNode.Parse(RecordedResponse)!;
        sixPoints["points"]!.AsArray().RemoveAt(6);
        var client = new ForecasterClient(new StubForecaster(HttpStatusCode.OK, sixPoints.ToJsonString()).CreateHttpClient());

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.ForecastAsync(BtcRequest(), TestContext.Current.CancellationToken));

        Assert.Contains("6 points", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Points_on_or_before_the_last_close_or_with_inverted_ranges_are_rejected()
    {
        var request = BtcRequest();
        var lastClose = request.Closes[^1].Date;
        var backtest = new ForecasterBacktest(8, 80m, 1m, 2m, BeatsBaseline: true);
        var stalePoint = new ForecasterPoint(lastClose, 100m, 90m, 110m, 80m, 120m);
        var invertedPoint = new ForecasterPoint(lastClose.AddDays(1), 100m, 110m, 90m, 80m, 120m);

        Assert.Throws<InvalidDataException>(() => ForecasterClient.EnsureAnswers(request with { HorizonSteps = 1 }, new ForecasterResponse("AutoETS", [stalePoint], backtest)));
        Assert.Throws<InvalidDataException>(() => ForecasterClient.EnsureAnswers(request with { HorizonSteps = 1 }, new ForecasterResponse("AutoETS", [invertedPoint], backtest)));
    }

    [Fact]
    public async Task A_refused_series_surfaces_the_forecasters_reason()
    {
        var detail = """{"detail":"Got 150 closes; at least 156 are needed to backtest 8 weeks and forecast 7 steps."}""";
        var client = new ForecasterClient(new StubForecaster(HttpStatusCode.UnprocessableEntity, detail).CreateHttpClient());

        var error = await Assert.ThrowsAsync<ForecasterRefusedException>(() => client.ForecastAsync(BtcRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(AssetKey.Parse("crypto:BTC"), error.Asset);
        Assert.Contains("at least 156", error.Message, StringComparison.Ordinal);
    }

    private static ForecasterRequest BtcRequest()
    {
        using var recorded = JsonDocument.Parse(RecordedRequest);
        var closes = recorded.RootElement.GetProperty("closes").EnumerateArray()
            .Select(close => new ForecasterClose(
                DateOnly.Parse(close.GetProperty("date").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(close.GetProperty("close").GetString()!, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        return new ForecasterRequest(AssetKey.Parse("crypto:BTC"), closes, ForecastFrequency.Daily, HorizonSteps: 7, BacktestWeeks: 8);
    }

    /// <summary>Property names and JSON value kinds, with arrays reduced to their first element.</summary>
    private static string Shape(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => $"{property.Name}:{Shape(property.Value)}")) + "}",
        JsonValueKind.Array => "[" + (element.GetArrayLength() > 0 ? Shape(element[0]) : "") + "]",
        _ => element.ValueKind.ToString(),
    };

    private sealed class StubForecaster(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        public Uri? RequestUri { get; private set; }

        public HttpClient CreateHttpClient() => new(this) { BaseAddress = new Uri("http://forecaster/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }
}
