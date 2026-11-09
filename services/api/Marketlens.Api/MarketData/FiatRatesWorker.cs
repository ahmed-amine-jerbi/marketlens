using Marketlens.Api.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marketlens.Api.MarketData;

/// <summary>
/// Keeps fiat currencies and the USDT rate (Tether's USD price, which converts every Binance price)
/// current: syncs the currency list into the catalogue, polls recent rates into the quote book and
/// daily candles, and backfills daily history once. Recent rates come first, so prices (and live
/// crypto) appear after one request on a fresh database. A failed poll keeps the last prices (they
/// age visibly) and retries with backoff.
/// </summary>
public sealed partial class FiatRatesWorker(
    FrankfurterClient frankfurter,
    AssetCatalog catalog,
    QuoteBook quoteBook,
    CandleWriter candleWriter,
    IServiceScopeFactory scopeFactory,
    IOptions<FrankfurterOptions> options,
    TimeProvider timeProvider,
    ILogger<FiatRatesWorker> logger) : BackgroundService
{
    /// <summary>Enough days back to find each currency's previous published rate across weekends and holidays.</summary>
    private const int RecentWindowDays = 7;

    /// <summary>Assets whose stored history says the backfill already ran, for fiat and for Tether.</summary>
    private static readonly AssetKey[] HistoryProbes = [AssetKey.Parse("fiat:EUR"), AssetKey.Tether];

    private readonly ProviderBackoff backoff = new();
    private bool isHistoryLoaded;

    /// <summary>
    /// Turns daily prices into one tick per currency: the latest day's price, measured against the
    /// day before it (D40, previous close).
    /// </summary>
    public static IEnumerable<PriceTick> ToLatestTicks(IEnumerable<DailyUsdPrice> prices) =>
        prices
            .GroupBy(price => price.Asset)
            .Select(days => days.OrderByDescending(day => day.Date).Take(2).ToList())
            .Select(latestTwo => new PriceTick(
                latestTwo[0].Asset,
                latestTwo[0].PriceUsd,
                latestTwo.Count > 1 ? latestTwo[1].PriceUsd : null,
                latestTwo[0].Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                FrankfurterClient.SourceName,
                Freshness.Daily));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await RefreshAsync(stoppingToken);
                backoff.Reset();
                delay = options.Value.PollInterval;
            }
            // Any provider failure, including an unexpected response shape, is logged and retried:
            // an exception escaping a background service would stop the whole API.
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                delay = backoff.NextDelay();
                LogRefreshFailed(logger, error, delay);
            }

            await Task.Delay(delay, timeProvider, stoppingToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var currencies = await frankfurter.FetchCurrenciesAsync(cancellationToken);
        await catalog.SyncFiatAsync(currencies, cancellationToken);
        var currencyCodes = currencies.Select(currency => currency.Code).ToHashSet();
        var quoteCodes = currencyCodes.Append(AssetKey.Tether.Symbol).ToHashSet();
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        var recentPrices = await frankfurter.FetchUsdPricesAsync(quoteCodes, today.AddDays(-RecentWindowDays), today, cancellationToken);
        await candleWriter.UpsertAsync(recentPrices.Select(ToDailyCandle), cancellationToken);
        var updatedCount = ToLatestTicks(recentPrices).Count(quoteBook.Apply);
        LogRefreshed(logger, currencyCodes.Count, updatedCount);

        if (!isHistoryLoaded)
        {
            await BackfillHistoryAsync(quoteCodes, today, cancellationToken);
            isHistoryLoaded = true;
        }
    }

    private async Task BackfillHistoryAsync(IReadOnlySet<string> quoteCodes, DateOnly today, CancellationToken cancellationToken)
    {
        var historyStart = today.AddYears(-options.Value.HistoryYears);
        if (await HasHistorySinceAsync(historyStart, cancellationToken))
        {
            return;
        }

        // One request per year keeps each response a few MB.
        for (var yearStart = historyStart; yearStart < today; yearStart = yearStart.AddYears(1))
        {
            var yearEnd = yearStart.AddYears(1).AddDays(-1);
            var prices = await frankfurter.FetchUsdPricesAsync(quoteCodes, yearStart, yearEnd < today ? yearEnd : today, cancellationToken);
            var writtenCount = await candleWriter.UpsertAsync(prices.Select(ToDailyCandle), cancellationToken);
            LogBackfilled(logger, yearStart, writtenCount);
        }
    }

    /// <summary>True when every probe already has a daily candle from the start of the history window.</summary>
    private async Task<bool> HasHistorySinceAsync(DateOnly historyStart, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        var earliestWanted = historyStart.AddDays(RecentWindowDays).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        foreach (var probeKey in HistoryProbes)
        {
            if (!catalog.TryGet(probeKey, out var probe))
            {
                return false;
            }

            var hasHistory = await db.PriceCandles.AnyAsync(
                candle => candle.AssetId == probe.Id && candle.Interval == CandleInterval.OneDay && candle.BucketStartUtc <= earliestWanted,
                cancellationToken);
            if (!hasHistory)
            {
                return false;
            }
        }

        return true;
    }

    private static CandleRow ToDailyCandle(DailyUsdPrice price) => new(
        price.Asset,
        CandleInterval.OneDay,
        price.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        price.PriceUsd,
        price.PriceUsd,
        price.PriceUsd,
        price.PriceUsd,
        Volume: null,
        FrankfurterClient.SourceName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fiat rates refreshed: {CurrencyCount} currencies, {UpdatedCount} new prices")]
    private static partial void LogRefreshed(ILogger logger, int currencyCount, int updatedCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fiat and USDT history backfilled from {YearStart}: {CandleCount} daily candles")]
    private static partial void LogBackfilled(ILogger logger, DateOnly yearStart, int candleCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fiat rate refresh from Frankfurter failed; retrying in {RetryDelay}")]
    private static partial void LogRefreshFailed(ILogger logger, Exception error, TimeSpan retryDelay);
}
