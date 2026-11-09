using Marketlens.Api.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marketlens.Api.Forecasts;

public enum TrackRecordSource
{
    /// <summary>From the forecaster's rolling backtest, until live evaluations span the whole window.</summary>
    Backtest,

    /// <summary>From stored forecasts compared with the closes that actually happened.</summary>
    Live,
}

/// <summary>
/// How the forecasts did: share of real closes inside the 80% range, and the typical miss next to
/// "no change"'s; <see cref="BeatsBaseline"/> is true when the forecasts' error is the lower one.
/// </summary>
public sealed record TrackRecord(TrackRecordSource Source, int WindowWeeks, decimal Coverage80Pct, decimal MeanErrorPct, decimal BaselineErrorPct, bool BeatsBaseline);

/// <summary>
/// Keeps the live track record: compares each stored forecast day, once its close exists, with what
/// happened, and summarises an asset's recent evaluations. Also deletes forecasts past retention.
/// </summary>
public sealed class ForecastEvaluator(IServiceScopeFactory scopeFactory, IOptions<ForecastOptions> options, TimeProvider timeProvider)
{
    private const int PercentDecimals = 4;

    /// <summary>The first evaluated day may fall a few days into the window (weekends, a forecast's first step).</summary>
    private const int WindowStartToleranceDays = 7;

    /// <summary>Scores one forecast day against its actual close; errors are absolute, in percent of the actual close.</summary>
    public static ForecastEvaluation Evaluate(Guid forecastId, ForecastPoint point, decimal lastCloseUsd, decimal actualCloseUsd)
    {
        ArgumentNullException.ThrowIfNull(point);
        return new ForecastEvaluation
        {
            ForecastId = forecastId,
            TargetDate = point.Date,
            ActualCloseUsd = actualCloseUsd,
            IsInsideRange80 = point.Low80Usd <= actualCloseUsd && actualCloseUsd <= point.High80Usd,
            ErrorPct = ErrorPct(point.MedianUsd, actualCloseUsd),
            BaselineErrorPct = ErrorPct(lastCloseUsd, actualCloseUsd),
        };
    }

    /// <summary>
    /// Evaluates every forecast day inside the track-record window whose close now exists and that
    /// has no evaluation yet. Safe to run again or concurrently: existing evaluations are kept.
    /// </summary>
    public async Task<int> EvaluateDueAsync(CancellationToken cancellationToken)
    {
        var today = Today();
        var windowStart = WindowStart(today);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var duePoints = await db.Forecasts
            .AsNoTracking()
            .SelectMany(forecast => forecast.Points, (forecast, point) => new { ForecastId = forecast.Id, forecast.AssetId, forecast.LastCloseUsd, Point = point })
            .Where(due => due.Point.Date < today && due.Point.Date >= windowStart)
            .Where(due => !db.ForecastEvaluations.Any(evaluation => evaluation.ForecastId == due.ForecastId && evaluation.TargetDate == due.Point.Date))
            .ToListAsync(cancellationToken);
        if (duePoints.Count == 0)
        {
            return 0;
        }

        var assetIds = duePoints.Select(due => due.AssetId).Distinct().ToList();
        var closeByAssetAndDate = await ReadDailyClosesAsync(db, assetIds, windowStart, today, cancellationToken);
        var evaluations = duePoints
            .Where(due => closeByAssetAndDate.ContainsKey((due.AssetId, due.Point.Date)))
            .Select(due => Evaluate(due.ForecastId, due.Point, due.LastCloseUsd, closeByAssetAndDate[(due.AssetId, due.Point.Date)]))
            .ToList();

        return await InsertNewAsync(db, evaluations, cancellationToken);
    }

    /// <summary>
    /// The asset's live track record over the window, or null until its evaluations span the whole
    /// window, so "over the last 8 weeks of our published forecasts" is never said of a few days.
    /// </summary>
    public async Task<TrackRecord?> ReadLiveTrackRecordAsync(MarketlensDbContext db, Guid assetId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var windowStart = WindowStart(Today());
        var summary = await (
                from evaluation in db.ForecastEvaluations
                join forecast in db.Forecasts on evaluation.ForecastId equals forecast.Id
                where forecast.AssetId == assetId && evaluation.TargetDate >= windowStart
                group evaluation by forecast.AssetId into evaluations
                select new
                {
                    Count = evaluations.Count(),
                    InsideCount = evaluations.Count(evaluation => evaluation.IsInsideRange80),
                    MeanErrorPct = evaluations.Average(evaluation => evaluation.ErrorPct),
                    BaselineErrorPct = evaluations.Average(evaluation => evaluation.BaselineErrorPct),
                    OldestTargetDate = evaluations.Min(evaluation => evaluation.TargetDate),
                })
            .SingleOrDefaultAsync(cancellationToken);
        if (summary is null || summary.OldestTargetDate > windowStart.AddDays(WindowStartToleranceDays))
        {
            return null;
        }

        var meanErrorPct = Math.Round(summary.MeanErrorPct, PercentDecimals);
        var baselineErrorPct = Math.Round(summary.BaselineErrorPct, PercentDecimals);
        return new TrackRecord(
            TrackRecordSource.Live,
            options.Value.BacktestWeeks,
            Math.Round(100m * summary.InsideCount / summary.Count, PercentDecimals),
            meanErrorPct,
            baselineErrorPct,
            BeatsBaseline: meanErrorPct < baselineErrorPct);
    }

    /// <summary>Deletes forecasts older than the retention period; their points and evaluations go with them.</summary>
    public async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var oldestKeptUtc = timeProvider.GetUtcNow().UtcDateTime.AddDays(-options.Value.RetentionDays);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        return await db.Forecasts.Where(forecast => forecast.CreatedAtUtc < oldestKeptUtc).ExecuteDeleteAsync(cancellationToken);
    }

    private static decimal ErrorPct(decimal forecastUsd, decimal actualCloseUsd) =>
        Math.Round(Math.Abs(forecastUsd - actualCloseUsd) / actualCloseUsd * 100m, PercentDecimals);

    private static async Task<Dictionary<(Guid AssetId, DateOnly Date), decimal>> ReadDailyClosesAsync(
        MarketlensDbContext db,
        List<Guid> assetIds,
        DateOnly fromDate,
        DateOnly toDateExclusive,
        CancellationToken cancellationToken)
    {
        var fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtc = toDateExclusive.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var closes = await db.PriceCandles
            .AsNoTracking()
            .Where(candle => assetIds.Contains(candle.AssetId) && candle.Interval == CandleInterval.OneDay && candle.BucketStartUtc >= fromUtc && candle.BucketStartUtc < toUtc)
            .Select(candle => new { candle.AssetId, candle.BucketStartUtc, candle.CloseUsd })
            .ToListAsync(cancellationToken);

        return closes.ToDictionary(close => (close.AssetId, DateOnly.FromDateTime(close.BucketStartUtc)), close => close.CloseUsd);
    }

    private static Task<int> InsertNewAsync(MarketlensDbContext db, List<ForecastEvaluation> evaluations, CancellationToken cancellationToken)
    {
        var forecastIds = evaluations.Select(evaluation => evaluation.ForecastId).ToArray();
        var targetDates = evaluations.Select(evaluation => evaluation.TargetDate).ToArray();
        var actualCloses = evaluations.Select(evaluation => evaluation.ActualCloseUsd).ToArray();
        var insideFlags = evaluations.Select(evaluation => evaluation.IsInsideRange80).ToArray();
        var errors = evaluations.Select(evaluation => evaluation.ErrorPct).ToArray();
        var baselineErrors = evaluations.Select(evaluation => evaluation.BaselineErrorPct).ToArray();

        // Parameterised arrays unnested into rows; a day another run already evaluated is left alone.
        return db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO forecast_evaluations (forecast_id, target_date, actual_close_usd, is_inside_range_80, error_pct, baseline_error_pct)
            SELECT * FROM unnest({forecastIds}, {targetDates}, {actualCloses}, {insideFlags}, {errors}, {baselineErrors})
            ON CONFLICT (forecast_id, target_date) DO NOTHING
            """,
            cancellationToken);
    }

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    private DateOnly WindowStart(DateOnly today) => today.AddDays(-7 * options.Value.BacktestWeeks);
}
