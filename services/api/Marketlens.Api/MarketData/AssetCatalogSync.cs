using Marketlens.Api.Data;

using Microsoft.EntityFrameworkCore;

namespace Marketlens.Api.MarketData;

/// <summary>
/// Makes the <c>assets</c> table match the asset universe at startup: adds new entries, updates
/// names and provider ids, and deactivates assets that were removed from the file (their history is
/// kept). Runs after <see cref="DatabaseMigrator"/>.
/// </summary>
public sealed partial class AssetCatalogSync(
    AssetUniverse universe,
    IServiceScopeFactory scopeFactory,
    ILogger<AssetCatalogSync> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();

        var listed = ListedAssets().ToDictionary(asset => asset.Key);
        var existing = await db.Assets
            .Where(asset => asset.Class != AssetClass.Fiat)
            .ToDictionaryAsync(asset => asset.Key, cancellationToken);

        var addedCount = 0;
        foreach (var (key, listedAsset) in listed)
        {
            if (!existing.TryGetValue(key, out var stored))
            {
                db.Assets.Add(listedAsset);
                addedCount++;
                continue;
            }

            stored.Name = listedAsset.Name;
            stored.Tracks = listedAsset.Tracks;
            stored.ProviderRefs = listedAsset.ProviderRefs;
            stored.IsActive = true;
        }

        var removed = existing.Values.Where(stored => stored.IsActive && !listed.ContainsKey(stored.Key)).ToList();
        foreach (var stored in removed)
        {
            stored.IsActive = false;
        }

        await db.SaveChangesAsync(cancellationToken);
        LogCatalogSynced(logger, listed.Count, addedCount, removed.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IEnumerable<Asset> ListedAssets()
    {
        foreach (var coin in universe.Crypto)
        {
            yield return NewAsset(AssetClass.Crypto, coin.Symbol, coin.Name, tracks: null,
                new ProviderRefs { BinanceSymbol = coin.BinanceSymbol, CoinGeckoId = coin.CoinGeckoId });
        }

        foreach (var stock in universe.Stocks)
        {
            yield return NewAsset(AssetClass.Stock, stock.Symbol, stock.Name, tracks: null,
                new ProviderRefs { FinnhubSymbol = stock.Symbol, TwelveDataSymbol = stock.Symbol });
        }

        foreach (var etf in universe.Etfs)
        {
            yield return NewAsset(AssetClass.Etf, etf.Symbol, etf.Name, etf.Tracks,
                new ProviderRefs { FinnhubSymbol = etf.Symbol, TwelveDataSymbol = etf.Symbol });
        }
    }

    private static Asset NewAsset(AssetClass assetClass, string symbol, string name, string? tracks, ProviderRefs providerRefs) => new()
    {
        Key = new AssetKey(assetClass, symbol).ToString(),
        Class = assetClass,
        Symbol = symbol,
        Name = name,
        Tracks = tracks,
        ProviderRefs = providerRefs,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Asset catalogue synced: {ListedCount} listed, {AddedCount} added, {DeactivatedCount} deactivated")]
    private static partial void LogCatalogSynced(ILogger logger, int listedCount, int addedCount, int deactivatedCount);
}
