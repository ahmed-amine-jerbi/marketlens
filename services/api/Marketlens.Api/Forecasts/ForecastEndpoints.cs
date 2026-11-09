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

    /// <summary>
    /// Enough history exists, but no current forecast does: the first hasn't been computed yet (D52),
    /// or the latest only covers days already past (the forecaster has been unreachable).
    /// </summary>
    Pending,
}

/// <summary>One forecast day in the display currency; <c>t</c> is midnight UTC, like history points.</summary>
public sealed record ForecastPointResponse(DateTime T, decimal Median, decimal Low80, decimal High80, decimal Low95, decimal High95);

public sealed record ForecastResponse(
    ForecastStatus Status,
    AssetKey In,
    string? Model,
    DateTime? CreatedAtUtc,
    int HorizonDays,
    IReadOnlyList<ForecastPointResponse> Points,
    TrackRecord? TrackRecord);

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

    public static ForecastStatus StatusOf(Forecast? current, bool hasEnoughHistory) => current switch
    {
        null when hasEnoughHistory => ForecastStatus.Pending,
        null => ForecastStatus.InsufficientHistory,
        _ => ForecastStatus.Available,
    };

    /// <summary>A forecast is current while at least one of its days is today or later.</summary>
    public static bool IsCurrent(Forecast forecast, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(forecast);
        return forecast.Points.Any(point => point.Date >= today);
    }

    private static async Task<Results<Ok<ForecastResponse>, ProblemHttpResult>> GetForecast(
        AssetCatalog catalog,
        QuoteBook quoteBook,
        MarketlensDbContext db,
        ForecastScheduler scheduler,
        ForecastEvaluator evaluator,
        IOptions<ForecastOptions> options,
        TimeProvider timeProvider,
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
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var current = latest is not null && IsCurrent(latest, today) ? latest : null;
        if (current is null)
        {
            var status = StatusOf(null, await scheduler.HasEnoughHistoryAsync(db, listing.Id, cancellationToken));
            return TypedResults.Ok(new ForecastResponse(status, displayIn, null, null, options.Value.HorizonDays, [], null));
        }

        var trackRecord = await evaluator.ReadLiveTrackRecordAsync(db, listing.Id, cancellationToken) ?? BacktestRecord(current.Backtest);
        var points = current.Points.OrderBy(point => point.Date).Select(point => InDisplayCurrency(point, displayQuote.PriceUsd)).ToList();

        return TypedResults.Ok(new ForecastResponse(
            StatusOf(current, hasEnoughHistory: true),
            displayIn,
            current.Model,
            DateTime.SpecifyKind(current.CreatedAtUtc, DateTimeKind.Utc),
            current.HorizonDays,
            points,
            trackRecord));
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
