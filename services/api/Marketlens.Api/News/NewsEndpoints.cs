using Marketlens.Api.Http;
using Marketlens.Api.MarketData;

using Microsoft.AspNetCore.Http.HttpResults;

namespace Marketlens.Api.News;

/// <summary>One article; the link and picture point to the publisher, who owns the content.</summary>
public sealed record NewsArticleResponse(long Id, string Headline, string? Summary, string Source, string Url, string? ImageUrl, DateTime PublishedAtUtc);

public sealed record NewsResponse(NewsStatus Status, IReadOnlyList<NewsArticleResponse> Items);

/// <summary>
/// <c>GET /api/v1/news</c>: stock-market and crypto news; <c>GET /api/v1/assets/{key}/news</c>: news
/// about one company. Both come from Finnhub through <see cref="NewsService"/>, newest first.
/// </summary>
public static class NewsEndpoints
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public static RouteGroupBuilder MapNewsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/news", GetMarketNews).WithName("GetMarketNews").WithTags("News")
            .WithSummary("Get the latest stock-market or crypto news, newest first");
        api.MapGet("/assets/{key}/news", GetAssetNews).WithName("GetAssetNews").WithTags("News")
            .WithSummary("Get the last week's news about a stock, newest first")
            .ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    public static bool TryParseCategory(string? text, out NewsCategory category)
    {
        (var isKnown, category) = (text ?? "markets") switch
        {
            "markets" => (true, NewsCategory.Markets),
            "crypto" => (true, NewsCategory.Crypto),
            "all" => (true, NewsCategory.All),
            _ => (false, default),
        };
        return isKnown;
    }

    private static async Task<Results<Ok<NewsResponse>, ProblemHttpResult>> GetMarketNews(
        NewsService news,
        string? category,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!TryParseCategory(category, out var newsCategory))
        {
            return ApiProblems.InvalidParameter("category", "Use markets, crypto or all.");
        }

        if (!IsValidLimit(limit))
        {
            return InvalidLimit();
        }

        return TypedResults.Ok(ToResponse(await news.GetMarketNewsAsync(newsCategory, cancellationToken), limit));
    }

    private static async Task<Results<Ok<NewsResponse>, ProblemHttpResult>> GetAssetNews(
        AssetCatalog catalog,
        NewsService news,
        string key,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!AssetKey.TryParse(key, out var asset))
        {
            return ApiProblems.InvalidParameter("key", "Use an asset key such as stock:AAPL.");
        }

        if (!catalog.TryGet(asset, out var listing))
        {
            return ApiProblems.UnknownAsset(asset);
        }

        if (!IsValidLimit(limit))
        {
            return InvalidLimit();
        }

        if (asset.Class != AssetClass.Stock || listing.ProviderRefs.FinnhubSymbol is not { } symbol)
        {
            return TypedResults.Ok(new NewsResponse(NewsStatus.NotCovered, []));
        }

        return TypedResults.Ok(ToResponse(await news.GetCompanyNewsAsync(symbol, cancellationToken), limit));
    }

    private static bool IsValidLimit(int? limit) => limit is null or (>= 1 and <= MaxLimit);

    private static ProblemHttpResult InvalidLimit() =>
        ApiProblems.InvalidParameter("limit", $"Use a number from 1 to {MaxLimit}.");

    private static NewsResponse ToResponse(NewsResult result, int? limit) => new(
        result.Status,
        result.Articles
            .Take(limit ?? DefaultLimit)
            .Select(article => new NewsArticleResponse(
                article.Id,
                article.Headline,
                article.Summary,
                article.Source,
                article.Url.AbsoluteUri,
                article.ImageUrl?.AbsoluteUri,
                DateTime.SpecifyKind(article.PublishedAtUtc, DateTimeKind.Utc)))
            .ToList());
}
