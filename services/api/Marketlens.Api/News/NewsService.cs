using System.Collections.Concurrent;

using Marketlens.Api.MarketData;

using Microsoft.Extensions.Options;

namespace Marketlens.Api.News;

public sealed class NewsOptions
{
    public const string Section = "News";

    /// <summary>
    /// How long a list of articles is served before Finnhub is asked again. News is fetched only when
    /// someone reads it, so this bounds the calls to a few an hour per category and per viewed stock.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How many days back a company's news is read.</summary>
    public int CompanyNewsDays { get; set; } = 7;
}

/// <summary>Which news a reader asked for: stock-market news, crypto news, or both merged.</summary>
public enum NewsCategory
{
    Markets,
    Crypto,
    All,
}

public enum NewsStatus
{
    /// <summary>Articles were fetched (the list may still be empty on a quiet day).</summary>
    Available,

    /// <summary>Finnhub failed and nothing was fetched before; the reader is told to try later.</summary>
    Unavailable,

    /// <summary>No Finnhub key is set, so no news is fetched.</summary>
    NoProviderKey,

    /// <summary>Not a stock: Finnhub's free company news covers North American companies only.</summary>
    NotCovered,
}

public sealed record NewsResult(NewsStatus Status, IReadOnlyList<NewsArticle> Articles);

/// <summary>
/// Market and company news from Finnhub, cached in memory per list for
/// <see cref="NewsOptions.CacheDuration"/> (D27). When Finnhub fails, the last list fetched keeps being
/// served; only a list never fetched reads "unavailable". One refresh runs at a time, so readers
/// arriving together cause one call.
/// </summary>
public sealed partial class NewsService(
    FinnhubClient finnhub,
    IOptions<NewsOptions> options,
    IOptions<FinnhubOptions> finnhubOptions,
    TimeProvider timeProvider,
    ILogger<NewsService> logger) : IDisposable
{
    private static readonly Dictionary<NewsCategory, string> FinnhubCategory = new()
    {
        [NewsCategory.Markets] = "general",
        [NewsCategory.Crypto] = "crypto",
    };

    private readonly ConcurrentDictionary<string, CachedNews> cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    public void Dispose() => refreshLock.Dispose();

    public async Task<NewsResult> GetMarketNewsAsync(NewsCategory category, CancellationToken cancellationToken)
    {
        if (!finnhubOptions.Value.HasApiKey)
        {
            return new NewsResult(NewsStatus.NoProviderKey, []);
        }

        if (category != NewsCategory.All)
        {
            var finnhubCategory = FinnhubCategory[category];
            return await GetCachedAsync($"category:{finnhubCategory}", token => finnhub.FetchMarketNewsAsync(finnhubCategory, token), cancellationToken);
        }

        var markets = await GetMarketNewsAsync(NewsCategory.Markets, cancellationToken);
        var crypto = await GetMarketNewsAsync(NewsCategory.Crypto, cancellationToken);
        var merged = markets.Articles.Concat(crypto.Articles).OrderByDescending(article => article.PublishedAtUtc).ToList();
        var isAnyAvailable = markets.Status == NewsStatus.Available || crypto.Status == NewsStatus.Available;
        return new NewsResult(isAnyAvailable ? NewsStatus.Available : NewsStatus.Unavailable, merged);
    }

    public async Task<NewsResult> GetCompanyNewsAsync(string finnhubSymbol, CancellationToken cancellationToken)
    {
        if (!finnhubOptions.Value.HasApiKey)
        {
            return new NewsResult(NewsStatus.NoProviderKey, []);
        }

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var fromDate = today.AddDays(-options.Value.CompanyNewsDays);
        return await GetCachedAsync($"company:{finnhubSymbol}", token => finnhub.FetchCompanyNewsAsync(finnhubSymbol, fromDate, today, token), cancellationToken);
    }

    private async Task<NewsResult> GetCachedAsync(string cacheKey, Func<CancellationToken, Task<IReadOnlyList<NewsArticle>>> fetch, CancellationToken cancellationToken)
    {
        if (TryGetFresh(cacheKey, out var fresh))
        {
            return new NewsResult(NewsStatus.Available, fresh);
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another reader may have refreshed the list while this one waited.
            if (TryGetFresh(cacheKey, out fresh))
            {
                return new NewsResult(NewsStatus.Available, fresh);
            }

            var articles = await fetch(cancellationToken);
            cache[cacheKey] = new CachedNews(articles, timeProvider.GetUtcNow().UtcDateTime);
            return new NewsResult(NewsStatus.Available, articles);
        }
        // A provider failure (HTTP error, rate limit, timeout, unexpected answer) is logged and the
        // last list is served; the next reader after the cache period tries again.
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            LogFetchFailed(logger, error, cacheKey);
            return cache.TryGetValue(cacheKey, out var stale)
                ? new NewsResult(NewsStatus.Available, stale.Articles)
                : new NewsResult(NewsStatus.Unavailable, []);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private bool TryGetFresh(string cacheKey, out IReadOnlyList<NewsArticle> articles)
    {
        if (cache.TryGetValue(cacheKey, out var cached) && timeProvider.GetUtcNow().UtcDateTime - cached.FetchedAtUtc < options.Value.CacheDuration)
        {
            articles = cached.Articles;
            return true;
        }

        articles = [];
        return false;
    }

    private sealed record CachedNews(IReadOnlyList<NewsArticle> Articles, DateTime FetchedAtUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fetching news ({CacheKey}) from Finnhub failed; serving the last list if any")]
    private static partial void LogFetchFailed(ILogger logger, Exception error, string cacheKey);
}
