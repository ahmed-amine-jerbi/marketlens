using Marketlens.Api.Data;

using Microsoft.Extensions.Options;

namespace Marketlens.Api.MarketData;

/// <summary>
/// Stands in for stock data when no Finnhub key is set, so a fresh clone shows a complete app (R17):
/// five years of daily history and gently moving prices for every stock and ETF, all tagged with
/// source <see cref="SourceName"/> so the UI labels them as sample data. The history is a random
/// walk seeded by the symbol, so it is the same on every start.
/// When a key is set, it removes its own candles once, so sample highs and lows never blend into
/// real history.
/// </summary>
public sealed partial class SampleStockFeed(
    AssetCatalog catalog,
    QuoteBook quoteBook,
    CandleWriter candleWriter,
    IOptions<FinnhubOptions> finnhubOptions,
    TimeProvider timeProvider,
    ILogger<SampleStockFeed> logger) : BackgroundService
{
    public const string SourceName = "sample";

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private static readonly DateOnly WalkStart = new(2020, 1, 1);

    private const double DailyVolatility = 0.015;
    private const double TickVolatility = 0.0004;

    /// <summary>Deterministic daily closes for a symbol from <see cref="WalkStart"/> through <paramref name="lastDay"/>.</summary>
    public static IReadOnlyList<(DateOnly Day, decimal CloseUsd)> DailyCloses(string symbol, DateOnly lastDay)
    {
        var seed = StableHash(symbol);
        var random = new Random(seed);
        var price = 20 + (seed % 58_000) / 100.0;
        var closes = new List<(DateOnly, decimal)>();

        for (var day = WalkStart; day <= lastDay; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            price *= Math.Exp(0.0002 + DailyVolatility * NextGaussian(random));
            closes.Add((day, decimal.Round((decimal)price, 2)));
        }

        return closes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listings = catalog.ActiveOf(AssetClass.Stock).Concat(catalog.ActiveOf(AssetClass.Etf)).ToList();
        try
        {
            if (finnhubOptions.Value.HasApiKey)
            {
                var removedQuoteCount = quoteBook.RemoveBySource(SourceName);
                var removedCandleCount = await candleWriter.DeleteBySourceAsync(SourceName, stoppingToken);
                LogSampleRemoved(logger, removedQuoteCount, removedCandleCount);
                return;
            }

            await WriteHistoryAsync(listings, DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime), stoppingToken);
            LogSampleMode(logger, listings.Count);
        }
        // A database failure here must not stop the API; prices still tick from memory below.
        catch (Exception error) when (!stoppingToken.IsCancellationRequested)
        {
            LogSampleHistoryFailed(logger, error);
        }

        var intradayRandom = new Random();
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var listing in listings)
            {
                quoteBook.Apply(NextTick(listing.Key, intradayRandom));
            }

            await Task.Delay(TickInterval, timeProvider, stoppingToken);
        }
    }

    private async Task WriteHistoryAsync(IReadOnlyList<CatalogEntry> listings, DateOnly today, CancellationToken cancellationToken)
    {
        var oldestWanted = today.AddDays(-(int)CandleWriter.RetentionWindow(CandleInterval.OneDay).TotalDays);
        var closesByListing = listings.ToDictionary(listing => listing.Key, listing => SampleStockFeed.DailyCloses(listing.Key.Symbol, today));

        // One batched write for every listing: a few seconds instead of fifty round trips.
        var candles = new List<CandleRow>();
        foreach (var (asset, closes) in closesByListing)
        {
            for (var index = 1; index < closes.Count; index++)
            {
                if (closes[index].Day >= oldestWanted)
                {
                    candles.Add(ToDailyCandle(asset, closes[index], openUsd: closes[index - 1].CloseUsd));
                }
            }
        }

        await candleWriter.UpsertAsync(candles, cancellationToken);

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var (asset, closes) in closesByListing)
        {
            quoteBook.Apply(new PriceTick(asset, closes[^1].CloseUsd, closes[^2].CloseUsd, nowUtc, SourceName, Freshness.Delayed));
        }
    }

    private PriceTick NextTick(AssetKey asset, Random random)
    {
        quoteBook.TryGet(asset, out var current);
        var lastPrice = (double)(current?.PriceUsd ?? 100m);
        var nextPrice = decimal.Round((decimal)(lastPrice * Math.Exp(TickVolatility * NextGaussian(random))), 2);
        return new PriceTick(asset, nextPrice, ReferencePriceUsd: null, timeProvider.GetUtcNow().UtcDateTime, SourceName, Freshness.Delayed);
    }

    private static CandleRow ToDailyCandle(AssetKey asset, (DateOnly Day, decimal CloseUsd) close, decimal openUsd) => new(
        asset,
        CandleInterval.OneDay,
        close.Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        openUsd,
        Math.Max(openUsd, close.CloseUsd),
        Math.Min(openUsd, close.CloseUsd),
        close.CloseUsd,
        Volume: null,
        SourceName);

    // Box-Muller: a standard normal sample from two uniform ones.
    private static double NextGaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    // FNV-1a, because string.GetHashCode differs between processes and the walk must not.
    private static int StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var character in text)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return (int)(hash & 0x7FFFFFFF);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No Finnhub key: {AssetCount} stocks and ETFs use labelled sample data")]
    private static partial void LogSampleMode(ILogger logger, int assetCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sample stock history could not be written or removed")]
    private static partial void LogSampleHistoryFailed(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Finnhub key set: removed {QuoteCount} sample prices and {CandleCount} sample candles")]
    private static partial void LogSampleRemoved(ILogger logger, int quoteCount, int candleCount);
}
