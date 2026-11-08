using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

using Marketlens.Api.Data;

using Microsoft.EntityFrameworkCore;

namespace Marketlens.Api.MarketData;

/// <summary>An active asset as the rest of the API sees it.</summary>
public sealed record CatalogEntry(Guid Id, AssetKey Key, string Name, string? Tracks, string? LogoUrl, ProviderRefs ProviderRefs);

/// <summary>A fiat currency as a provider lists it, before it enters the catalogue.</summary>
public sealed record FiatCurrency(string Code, string Name);

/// <summary>
/// Owns the <c>assets</c> table. Writes the listed coins, stocks and ETFs at startup and the fiat
/// currencies whenever the fiat provider reports them, deactivating entries that disappear (their
/// history is kept). Holds the active assets in memory, so ingestion resolves an asset key to its
/// id without a query.
/// </summary>
public sealed partial class AssetCatalog(
    AssetUniverse universe,
    IServiceScopeFactory scopeFactory,
    ILogger<AssetCatalog> logger) : IHostedService, IDisposable
{
    private static readonly AssetClass[] ListedClasses = [AssetClass.Crypto, AssetClass.Stock, AssetClass.Etf];
    private static readonly AssetClass[] FiatClass = [AssetClass.Fiat];

    private readonly SemaphoreSlim writeLock = new(1, 1);
    private FrozenDictionary<AssetKey, CatalogEntry> activeAssets = FrozenDictionary<AssetKey, CatalogEntry>.Empty;

    public async Task StartAsync(CancellationToken cancellationToken) => await SyncListedAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => writeLock.Dispose();

    public bool TryGet(AssetKey key, [MaybeNullWhen(false)] out CatalogEntry entry) => activeAssets.TryGetValue(key, out entry);

    /// <summary>Active assets of one class, or of every class when <paramref name="assetClass"/> is null.</summary>
    public IEnumerable<CatalogEntry> ActiveOf(AssetClass? assetClass) =>
        activeAssets.Values.Where(entry => assetClass is null || entry.Key.Class == assetClass);

    public Task SyncListedAsync(CancellationToken cancellationToken) =>
        SyncAsync(ListedClasses, ListedAssets(), cancellationToken);

    public Task SyncFiatAsync(IEnumerable<FiatCurrency> currencies, CancellationToken cancellationToken) =>
        SyncAsync(FiatClass, currencies.Select(currency => NewAsset(AssetClass.Fiat, currency.Code, currency.Name, tracks: null, new ProviderRefs())), cancellationToken);

    /// <summary>Stores logo URLs for the given assets and leaves the rest untouched.</summary>
    public async Task UpdateLogosAsync(IReadOnlyDictionary<AssetKey, string> logoByAsset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logoByAsset);

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
            var keys = logoByAsset.Keys.Select(key => key.ToString()).ToList();

            foreach (var asset in await db.Assets.Where(asset => keys.Contains(asset.Key)).ToListAsync(cancellationToken))
            {
                asset.LogoUrl = logoByAsset[AssetKey.Parse(asset.Key)];
            }

            await db.SaveChangesAsync(cancellationToken);
            await ReloadAsync(db, cancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task SyncAsync(AssetClass[] ownedClasses, IEnumerable<Asset> listedAssets, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();

            var listed = listedAssets.ToDictionary(asset => asset.Key);
            var existing = await db.Assets
                .Where(asset => ownedClasses.Contains(asset.Class))
                .ToDictionaryAsync(asset => asset.Key, cancellationToken);

            var addedCount = AddOrUpdate(db, listed, existing);
            var deactivatedCount = Deactivate(existing.Values.Where(stored => stored.IsActive && !listed.ContainsKey(stored.Key)));

            await db.SaveChangesAsync(cancellationToken);
            await ReloadAsync(db, cancellationToken);
            LogCatalogSynced(logger, ownedClasses, listed.Count, addedCount, deactivatedCount);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static int AddOrUpdate(MarketlensDbContext db, Dictionary<string, Asset> listed, Dictionary<string, Asset> existing)
    {
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

        return addedCount;
    }

    private static int Deactivate(IEnumerable<Asset> removedAssets)
    {
        var deactivatedCount = 0;
        foreach (var removed in removedAssets)
        {
            removed.IsActive = false;
            deactivatedCount++;
        }

        return deactivatedCount;
    }

    private async Task ReloadAsync(MarketlensDbContext db, CancellationToken cancellationToken)
    {
        var active = await db.Assets.AsNoTracking().Where(asset => asset.IsActive).ToListAsync(cancellationToken);
        activeAssets = active
            .Select(asset => new CatalogEntry(asset.Id, AssetKey.Parse(asset.Key), asset.Name, asset.Tracks, asset.LogoUrl, asset.ProviderRefs))
            .ToFrozenDictionary(entry => entry.Key);
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Asset catalogue synced ({AssetClasses}): {ListedCount} listed, {AddedCount} added, {DeactivatedCount} deactivated")]
    private static partial void LogCatalogSynced(ILogger logger, IReadOnlyList<AssetClass> assetClasses, int listedCount, int addedCount, int deactivatedCount);
}
