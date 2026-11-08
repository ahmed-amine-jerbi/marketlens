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
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();

        var quotes = quoteBook.Snapshot()
            .Select(quote => catalog.TryGet(quote.Asset, out var entry) ? (entry.Id, Quote: quote) : default)
            .Where(row => row.Quote is not null)
            .ToList();
        var existing = await db.QuoteSnapshots.ToDictionaryAsync(snapshot => snapshot.AssetId, cancellationToken);

        foreach (var (assetId, quote) in quotes)
        {
            if (!existing.TryGetValue(assetId, out var snapshot))
            {
                db.QuoteSnapshots.Add(new QuoteSnapshot
                {
                    AssetId = assetId,
                    PriceUsd = quote.PriceUsd,
                    ReferencePriceUsd = quote.ReferencePriceUsd,
                    AsOfUtc = quote.AsOfUtc,
                    Source = quote.Source,
                    Freshness = quote.Freshness,
                });
                continue;
            }

            snapshot.PriceUsd = quote.PriceUsd;
            snapshot.ReferencePriceUsd = quote.ReferencePriceUsd;
            snapshot.AsOfUtc = quote.AsOfUtc;
            snapshot.Source = quote.Source;
            snapshot.Freshness = quote.Freshness;
        }

        await db.SaveChangesAsync(cancellationToken);
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
