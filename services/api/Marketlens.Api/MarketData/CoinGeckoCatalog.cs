using System.Text.Json;

using Microsoft.Extensions.Options;

namespace Marketlens.Api.MarketData;

public sealed class CoinGeckoOptions
{
    public const string Section = "MarketData:CoinGecko";

    /// <summary>From <c>COINGECKO_DEMO_API_KEY</c>. Without it, coins show without logos.</summary>
    public string? ApiKey { get; set; }

    public Uri BaseUrl { get; set; } = new("https://api.coingecko.com/api/v3/");

    /// <summary>One call a day for all coins: ~30 of the Demo plan's 10,000 calls a month.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromDays(1);

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Adapter for CoinGecko's Demo API, used only for coin logos (D28); prices and history come from
/// Binance. The UI credits "Data provided by CoinGecko" wherever these logos appear (D45).
/// </summary>
public sealed class CoinGeckoClient(HttpClient http)
{
    public const string ApiKeyHeader = "x-cg-demo-api-key";

    private sealed record MarketDto(string Id, string Image);

    /// <returns>Logo URL by CoinGecko id, for the ids CoinGecko knows.</returns>
    public async Task<IReadOnlyDictionary<string, string>> FetchLogoUrlsAsync(IEnumerable<string> coinGeckoIds, CancellationToken cancellationToken)
    {
        var ids = string.Join(',', coinGeckoIds.Select(Uri.EscapeDataString));
        using var response = await http.GetAsync($"coins/markets?vs_currency=usd&ids={ids}&per_page=250", cancellationToken);
        ProviderRateLimitedException.ThrowIfLimited(response, "coingecko");
        response.EnsureSuccessStatusCode();

        var markets = await response.Content.ReadFromJsonAsync<List<MarketDto>>(JsonSerializerOptions.Web, cancellationToken)
            ?? throw new JsonException("CoinGecko returned an empty market list.");

        return markets
            .Where(market => Uri.TryCreate(market.Image, UriKind.Absolute, out var logo) && logo.Scheme == Uri.UriSchemeHttps)
            .ToDictionary(market => market.Id, market => market.Image);
    }
}

/// <summary>Refreshes coin logos once a day. Only runs with a CoinGecko Demo key.</summary>
public sealed partial class CoinGeckoCatalog(
    CoinGeckoClient coinGecko,
    AssetCatalog catalog,
    IOptions<CoinGeckoOptions> options,
    TimeProvider timeProvider,
    ILogger<CoinGeckoCatalog> logger) : BackgroundService
{
    private readonly ProviderBackoff backoff = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.HasApiKey)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                await RefreshLogosAsync(stoppingToken);
                backoff.Reset();
                delay = options.Value.RefreshInterval;
            }
            // Any provider failure, including an unexpected response shape, is logged and retried:
            // an exception escaping a background service would stop the whole API.
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                delay = backoff.NextDelay((error as ProviderRateLimitedException)?.RetryAfter);
                LogRefreshFailed(logger, error, delay);
            }

            await Task.Delay(delay, timeProvider, stoppingToken);
        }
    }

    private async Task RefreshLogosAsync(CancellationToken cancellationToken)
    {
        var coinByGeckoId = catalog.ActiveOf(AssetClass.Crypto)
            .Where(coin => coin.ProviderRefs.CoinGeckoId is not null)
            .ToDictionary(coin => coin.ProviderRefs.CoinGeckoId!, coin => coin.Key);

        var logoById = await coinGecko.FetchLogoUrlsAsync(coinByGeckoId.Keys, cancellationToken);
        var logoByAsset = logoById.ToDictionary(pair => coinByGeckoId[pair.Key], pair => pair.Value);
        await catalog.UpdateLogosAsync(logoByAsset, cancellationToken);

        foreach (var unknownId in coinByGeckoId.Keys.Except(logoById.Keys))
        {
            LogUnknownCoinGeckoId(logger, coinByGeckoId[unknownId], unknownId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "CoinGecko has no coin '{CoinGeckoId}' for {Asset}; check its coinGeckoId in assets.json")]
    private static partial void LogUnknownCoinGeckoId(ILogger logger, AssetKey asset, string coinGeckoId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "CoinGecko logo refresh failed; retrying in {RetryDelay}")]
    private static partial void LogRefreshFailed(ILogger logger, Exception error, TimeSpan retryDelay);
}
