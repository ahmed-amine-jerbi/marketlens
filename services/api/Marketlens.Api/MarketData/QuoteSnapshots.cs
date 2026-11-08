using Marketlens.Api.Data;

using Microsoft.EntityFrameworkCore;

namespace Marketlens.Api.MarketData;

/// <summary>
/// Saves the quote book to <c>quote_snapshots</c> every minute and loads it back at startup, before
/// any provider connects, so a restart shows the last known prices (with their real age) instead of
/// an empty app.
/// </summary>
public sealed partial class QuoteSnapshots(
    AssetCatalog catalog,
    QuoteBook quoteBook,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<QuoteSnapshots> logger) : BackgroundService
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(1);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var restoredCount = await RestoreAsync(cancellationToken);
        LogRestored(logger, restoredCount);
        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await SaveAsync(cancellationToken);
    }

    public async Task<int> RestoreAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();

        var saved = await db.QuoteSnapshots
            .AsNoTracking()
            .Join(db.Assets.Where(asset => asset.IsActive), snapshot => snapshot.AssetId, asset => asset.Id, (snapshot, asset) => new { asset.Key, Snapshot = snapshot })
            .ToListAsync(cancellationToken);

        var restored = saved.Select(row => new UsdQuote(
            AssetKey.Parse(row.Key),
            row.Snapshot.PriceUsd,
            row.Snapshot.ReferencePriceUsd,
            DateTime.SpecifyKind(row.Snapshot.AsOfUtc, DateTimeKind.Utc),
            row.Snapshot.Source,
            row.Snapshot.Freshness)).ToList();

        quoteBook.Restore(restored);
        return restored.Count;
    }

    public async Task<int> SaveAsync(CancellationToken cancellationToken)
    {
        var quotes = quoteBook.Snapshot()
            .Select(quote => (Quote: quote, IsListed: catalog.TryGet(quote.Asset, out var entry), AssetId: entry?.Id ?? Guid.Empty))
            .Where(row => row.IsListed)
            .ToList();
        if (quotes.Count == 0)
        {
            return 0;
        }

        var assetIds = quotes.Select(row => row.AssetId).ToArray();
        var prices = quotes.Select(row => row.Quote.PriceUsd).ToArray();
        var references = quotes.Select(row => row.Quote.ReferencePriceUsd).ToArray();
        var asOfTimes = quotes.Select(row => DateTime.SpecifyKind(row.Quote.AsOfUtc, DateTimeKind.Utc)).ToArray();
        var sources = quotes.Select(row => row.Quote.Source).ToArray();
        var freshness = quotes.Select(row => row.Quote.Freshness.ToString().ToLowerInvariant()).ToArray();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();

        // One atomic upsert: safe if two processes ever save at the same moment.
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO quote_snapshots (asset_id, price_usd, reference_price_usd, as_of_utc, source, freshness)
            SELECT * FROM unnest({assetIds}, {prices}, {references}, {asOfTimes}, {sources}, {freshness})
            ON CONFLICT (asset_id) DO UPDATE SET
                price_usd = EXCLUDED.price_usd,
                reference_price_usd = EXCLUDED.reference_price_usd,
                as_of_utc = EXCLUDED.as_of_utc,
                source = EXCLUDED.source,
                freshness = EXCLUDED.freshness
            WHERE quote_snapshots.as_of_utc <= EXCLUDED.as_of_utc
            """,
            cancellationToken);
        return quotes.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(SaveInterval, timeProvider, stoppingToken);
            try
            {
                await SaveAsync(stoppingToken);
            }
            // A failed save is retried next minute; an exception escaping a background service
            // would stop the whole API.
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                LogSaveFailed(logger, error);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Restored {QuoteCount} quotes from the last run")]
    private static partial void LogRestored(ILogger logger, int quoteCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saving quote snapshots failed; retrying next minute")]
    private static partial void LogSaveFailed(ILogger logger, Exception error);
}
