using System.Text.Json;

using Marketlens.Api.Data;
using Marketlens.Api.MarketData;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marketlens.Api.Forecasts;

/// <summary>
/// Keeps every asset's forecast current (D55). Each check, at startup and then every
/// <see cref="ForecastOptions.CheckInterval"/>, first evaluates past forecasts, then forecasts each
/// asset with a complete daily close newer than its latest forecast's input. So forecasts follow
/// the daily close, and a fresh database fills within minutes of its history arriving.
/// </summary>
public sealed partial class ForecastScheduler(
    ForecasterClient forecaster,
    ForecastEvaluator evaluator,
    AssetCatalog catalog,
    IServiceScopeFactory scopeFactory,
    IOptions<ForecastOptions> options,
    TimeProvider timeProvider,
    ILogger<ForecastScheduler> logger) : BackgroundService
{
    /// <summary>The first closes in the history window may fall a few days after it opens (weekends, holidays).</summary>
    private const int HistoryStartToleranceDays = 7;

    /// <summary>A fresh database gets its first forecasts for the few coins and funds before ~160 currencies.</summary>
    private static readonly AssetClass[] ForecastOrder = [AssetClass.Crypto, AssetClass.Etf, AssetClass.Stock, AssetClass.Fiat];

    public static ForecastFrequency FrequencyOf(AssetClass assetClass) =>
        assetClass == AssetClass.Crypto ? ForecastFrequency.Daily : ForecastFrequency.BusinessDays;

    /// <summary>Steps of the series' calendar inside the next <paramref name="horizonDays"/> calendar days.</summary>
    public static int HorizonSteps(ForecastFrequency frequency, DateOnly seriesEnd, int horizonDays) =>
        frequency == ForecastFrequency.Daily
            ? horizonDays
            : Enumerable.Range(1, horizonDays).Count(day => IsWeekday(seriesEnd.AddDays(day)));

    /// <summary>Whether an asset's daily closes reach back far enough to be forecast; the endpoint's "pending" versus "insufficient history".</summary>
    public async Task<bool> HasEnoughHistoryAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var historyStartUtc = HistoryStart(Today()).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var latestAcceptedStartUtc = historyStartUtc.AddDays(HistoryStartToleranceDays);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        return await db.PriceCandles.AnyAsync(
            candle => candle.AssetId == assetId && candle.Interval == CandleInterval.OneDay && candle.BucketStartUtc >= historyStartUtc && candle.BucketStartUtc <= latestAcceptedStartUtc,
            cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            // The forecaster unreachable, or the database briefly gone: logged and retried at the next
            // check. An exception escaping a background service would stop the whole API.
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                LogRunFailed(logger, error, options.Value.CheckInterval);
            }

            await Task.Delay(options.Value.CheckInterval, timeProvider, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var evaluatedCount = await evaluator.EvaluateDueAsync(cancellationToken);
        var prunedCount = await evaluator.PruneAsync(cancellationToken);
        var today = Today();
        var staleAssets = await FindStaleAsync(today, cancellationToken);

        var forecastCount = 0;
        foreach (var listing in staleAssets)
        {
            // Failures that concern one series are logged and the run moves on; the forecaster being
            // unreachable (HttpRequestException) ends the run, since every other asset would fail too.
            try
            {
                forecastCount += await ForecastAsync(listing, today, cancellationToken) ? 1 : 0;
            }
            catch (ForecasterRefusedException error)
            {
                LogRefused(logger, listing.Key, error.Message);
            }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            {
                LogInvalidAnswer(logger, error, listing.Key);
            }
            catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
            {
                LogTimedOut(logger, error, listing.Key, options.Value.RequestTimeout);
            }
        }

        LogRun(logger, staleAssets.Count, forecastCount, evaluatedCount, prunedCount);
    }

    /// <summary>Active assets whose newest complete close (on their own calendar) is newer than their latest forecast's input.</summary>
    private async Task<List<CatalogEntry>> FindStaleAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var todayUtc = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var newestCloseByAsset = await db.PriceCandles
            .Where(candle => candle.Interval == CandleInterval.OneDay && candle.BucketStartUtc < todayUtc)
            .GroupBy(candle => candle.AssetId)
            .Select(closes => new { AssetId = closes.Key, NewestUtc = closes.Max(candle => candle.BucketStartUtc) })
            .ToDictionaryAsync(newest => newest.AssetId, newest => DateOnly.FromDateTime(newest.NewestUtc), cancellationToken);
        var forecastEndByAsset = await db.Forecasts
            .GroupBy(forecast => forecast.AssetId)
            .Select(forecasts => new { AssetId = forecasts.Key, SeriesEnd = forecasts.Max(forecast => forecast.SeriesEndDate) })
            .ToDictionaryAsync(latest => latest.AssetId, latest => latest.SeriesEnd, cancellationToken);

        return ForecastOrder
            .SelectMany(assetClass => catalog.ActiveOf(assetClass))
            .Where(listing => newestCloseByAsset.TryGetValue(listing.Id, out var newestClose)
                && (!forecastEndByAsset.TryGetValue(listing.Id, out var forecastEnd)
                    || LastTradingDay(newestClose, FrequencyOf(listing.Key.Class)) > forecastEnd))
            .ToList();
    }

    /// <summary>Forecasts one asset and stores the result; false when its history is too short.</summary>
    private async Task<bool> ForecastAsync(CatalogEntry listing, DateOnly today, CancellationToken cancellationToken)
    {
        var frequency = FrequencyOf(listing.Key.Class);
        var historyStart = HistoryStart(today);
        var closes = await ReadClosesAsync(listing.Id, historyStart, today, frequency, cancellationToken);
        if (closes.Count == 0 || closes[0].Date > historyStart.AddDays(HistoryStartToleranceDays))
        {
            return false;
        }

        var lastClose = closes[^1];
        var request = new ForecasterRequest(
            listing.Key,
            closes,
            frequency,
            HorizonSteps(frequency, lastClose.Date, options.Value.HorizonDays),
            options.Value.BacktestWeeks);
        var answer = await forecaster.ForecastAsync(request, cancellationToken);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        db.Forecasts.Add(ToForecast(listing.Id, lastClose, answer));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Complete daily closes in the history window, on the series' calendar (weekdays only for business days).</summary>
    private async Task<List<ForecasterClose>> ReadClosesAsync(Guid assetId, DateOnly fromDate, DateOnly toDateExclusive, ForecastFrequency frequency, CancellationToken cancellationToken)
    {
        var fromUtc = fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtc = toDateExclusive.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var closes = await db.PriceCandles
            .AsNoTracking()
            .Where(candle => candle.AssetId == assetId && candle.Interval == CandleInterval.OneDay && candle.BucketStartUtc >= fromUtc && candle.BucketStartUtc < toUtc)
            .OrderBy(candle => candle.BucketStartUtc)
            .Select(candle => new { candle.BucketStartUtc, candle.CloseUsd })
            .ToListAsync(cancellationToken);

        return closes
            .Select(close => new ForecasterClose(DateOnly.FromDateTime(close.BucketStartUtc), close.CloseUsd))
            .Where(close => frequency == ForecastFrequency.Daily || IsWeekday(close.Date))
            .ToList();
    }

    private Forecast ToForecast(Guid assetId, ForecasterClose lastClose, ForecasterResponse answer) => new()
    {
        AssetId = assetId,
        CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        SeriesEndDate = lastClose.Date,
        LastCloseUsd = lastClose.Close,
        Model = answer.Model,
        HorizonDays = options.Value.HorizonDays,
        Backtest = new ForecastBacktest
        {
            WindowWeeks = answer.Backtest.WindowWeeks,
            Coverage80Pct = answer.Backtest.Coverage80Pct,
            MeanErrorPct = answer.Backtest.MeanErrorPct,
            BaselineErrorPct = answer.Backtest.BaselineErrorPct,
            BeatsBaseline = answer.Backtest.BeatsBaseline,
        },
        Points = answer.Points.Select(point => new ForecastPoint
        {
            Date = point.Date,
            MedianUsd = point.Median,
            Low80Usd = point.Low80,
            High80Usd = point.High80,
            Low95Usd = point.Low95,
            High95Usd = point.High95,
        }).ToList(),
    };

    /// <summary>The last day a series on this calendar can close on: a weekend close counts as Friday's for business days.</summary>
    private static DateOnly LastTradingDay(DateOnly date, ForecastFrequency frequency) => (frequency, date.DayOfWeek) switch
    {
        (ForecastFrequency.BusinessDays, DayOfWeek.Saturday) => date.AddDays(-1),
        (ForecastFrequency.BusinessDays, DayOfWeek.Sunday) => date.AddDays(-2),
        _ => date,
    };

    private static bool IsWeekday(DateOnly date) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    private DateOnly HistoryStart(DateOnly today) => today.AddDays(-options.Value.HistoryDays);

    [LoggerMessage(Level = LogLevel.Information, Message = "Forecast check: {StaleCount} assets due, {ForecastCount} forecast, {EvaluatedCount} days evaluated, {PrunedCount} old forecasts deleted")]
    private static partial void LogRun(ILogger logger, int staleCount, int forecastCount, int evaluatedCount, int prunedCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "No forecast for {Asset}: {Reason}")]
    private static partial void LogRefused(ILogger logger, AssetKey asset, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The forecaster's answer for {Asset} was rejected; it is retried at the next check")]
    private static partial void LogInvalidAnswer(ILogger logger, Exception error, AssetKey asset);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The forecast for {Asset} took longer than {Timeout}; it is retried at the next check")]
    private static partial void LogTimedOut(ILogger logger, Exception error, AssetKey asset, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Forecast check failed; retrying in {RetryDelay}")]
    private static partial void LogRunFailed(ILogger logger, Exception error, TimeSpan retryDelay);
}
