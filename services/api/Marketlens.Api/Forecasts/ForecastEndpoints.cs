using Marketlens.Api.Conversions;
using Marketlens.Api.Data;
using Marketlens.Api.Http;
using Marketlens.Api.MarketData;
using Marketlens.Api.Quotes;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marketlens.Api.Forecasts;

public enum ForecastStatus
{
    /// <summary>A forecast exists; the track record says whether it beat "no change" (D57).</summary>
    Available,

    /// <summary>The asset's daily closes don't reach back <see cref="ForecastOptions.HistoryDays"/>.</summary>
    InsufficientHistory,

    /// <summary>Enough history exists, but the first forecast hasn't been computed yet (D52).</summary>
    Pending,
}

/// <summary>One forecast day in the display currency; <c>t</c> is midnight UTC, like history points.</summary>
public sealed record ForecastPointResponse(DateTime T, decimal Median, decimal Low80, decimal High80, decimal Low95, decimal High95);

/// <summary>How the forecasts did: share of real closes inside the 80% range, and errors next to "no change".</summary>
public sealed record TrackRecordResponse(TrackRecordSource Source, int WindowWeeks, decimal Coverage80Pct, decimal MeanErrorPct, decimal BaselineErrorPct, bool BeatsBaseline);

public sealed record ForecastResponse(
    ForecastStatus Status,
    AssetKey In,
    string? Model,
    DateTime? CreatedAtUtc,
    int HorizonDays,
    IReadOnlyList<ForecastPointResponse> Points,
    TrackRecordResponse? TrackRecord);

/// <summary>
/// <c>GET /api/v1/assets/{key}/forecast</c>: the asset's latest forecast, shown in the display currency
/// at that currency's latest price (D51), with its track record. The range is shown whether or not it
/// beat "no change"; the track record says which (D57).
/// </summary>
public static class ForecastEndpoints
{
    public static RouteGroupBuilder MapForecastEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/assets/{key}/forecast", GetForecast).WithName("GetAssetForecast").WithTags("Forecasts")
            .WithSummary("Get an asset's forecast range for the next days and its track record")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    public static ForecastStatus StatusOf(Forecast? latest, bool hasEnoughHistory) => latest switch
    {
        null when hasEnoughHistory => ForecastStatus.Pending,
        null => ForecastStatus.InsufficientHistory,
        _ => ForecastStatus.Available,
    };

    private static async Task<Results<Ok<ForecastResponse>, ProblemHttpResult>> GetForecast(
        AssetCatalog catalog,
        QuoteBook quoteBook,
        MarketlensDbContext db,
        ForecastScheduler scheduler,
        ForecastEvaluator evaluator,
        IOptions<ForecastOptions> options,
        string key,
        string? @in,
        CancellationToken cancellationToken)
    {
        if (!AssetKey.TryParse(key, out var asset))
        {
            return ApiProblems.InvalidParameter("key", "Use an asset key such as crypto:BTC.");
        }

        if (!catalog.TryGet(asset, out var listing))
        {
            return ApiProblems.UnknownAsset(asset);
        }

        if (!QuoteEndpoints.TryParseDisplayCurrency(catalog, @in, out var displayIn, out var invalidIn))
        {
            return invalidIn;
        }

        if (!quoteBook.TryGet(displayIn, out var displayQuote))
        {
            return ApiProblems.RateUnavailable(displayIn);
        }

        var latest = await db.Forecasts
            .AsNoTracking()
            .Include(forecast => forecast.Points)
            .Where(forecast => forecast.AssetId == listing.Id)
            .OrderByDescending(forecast => forecast.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var status = StatusOf(latest, latest is null && await scheduler.HasEnoughHistoryAsync(listing.Id, cancellationToken));
        if (latest is null)
        {
            return TypedResults.Ok(new ForecastResponse(status, displayIn, null, null, options.Value.HorizonDays, [], null));
        }

        var trackRecord = await evaluator.ReadLiveTrackRecordAsync(listing.Id, cancellationToken) ?? BacktestRecord(latest.Backtest);
        var points = latest.Points.OrderBy(point => point.Date).Select(point => InDisplayCurrency(point, displayQuote.PriceUsd)).ToList();

        return TypedResults.Ok(new ForecastResponse(
            status,
            displayIn,
            latest.Model,
            DateTime.SpecifyKind(latest.CreatedAtUtc, DateTimeKind.Utc),
            latest.HorizonDays,
            points,
            new TrackRecordResponse(trackRecord.Source, trackRecord.WindowWeeks, trackRecord.Coverage80Pct, trackRecord.MeanErrorPct, trackRecord.BaselineErrorPct, trackRecord.BeatsBaseline)));
    }

    private static TrackRecord BacktestRecord(ForecastBacktest backtest) =>
        new(TrackRecordSource.Backtest, backtest.WindowWeeks, backtest.Coverage80Pct, backtest.MeanErrorPct, backtest.BaselineErrorPct, backtest.BeatsBaseline);

    private static ForecastPointResponse InDisplayCurrency(ForecastPoint point, decimal displayPriceUsd) => new(
        point.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        ConversionCalculator.Rate(point.MedianUsd, displayPriceUsd),
        ConversionCalculator.Rate(point.Low80Usd, displayPriceUsd),
        ConversionCalculator.Rate(point.High80Usd, displayPriceUsd),
        ConversionCalculator.Rate(point.Low95Usd, displayPriceUsd),
        ConversionCalculator.Rate(point.High95Usd, displayPriceUsd));
}
