using System.Diagnostics.CodeAnalysis;

using Marketlens.Api.Http;
using Marketlens.Api.MarketData;

using Microsoft.AspNetCore.Http.HttpResults;

namespace Marketlens.Api.Quotes;

public sealed record AssetResponse(AssetKey Key, AssetClass Class, string Symbol, string Name, string? LogoUrl, string? Tracks);

public sealed record AssetPage(IReadOnlyList<AssetResponse> Items, string? NextCursor);

public sealed record AssetDetailResponse(AssetResponse Asset, QuoteResponse? Quote);

/// <summary>Public read endpoints for the asset catalogue and quotes: <c>/api/v1/assets</c>, <c>/api/v1/quotes</c>.</summary>
public static class QuoteEndpoints
{
    public const int MaxKeysPerRequest = 50;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    public const int MaxSearchLength = 60;

    public static RouteGroupBuilder MapQuoteEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/assets", ListAssets).WithName("ListAssets").WithTags("Assets")
            .WithSummary("List tracked assets, filtered by class or text, in pages ordered by key");
        api.MapGet("/assets/{key}", GetAsset).WithName("GetAsset").WithTags("Assets")
            .WithSummary("Get one asset with its latest quote in a display currency")
            .ProducesProblem(StatusCodes.Status404NotFound);
        api.MapGet("/quotes", GetQuotes).WithName("GetQuotes").WithTags("Quotes")
            .WithSummary("Get the latest quotes of up to 50 assets in one display currency")
            .ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    /// <summary>Pages through active assets in key order; <paramref name="cursor"/> is the last key of the previous page.</summary>
    private static Results<Ok<AssetPage>, ProblemHttpResult> ListAssets(AssetCatalog catalog, string? @class, string? search, string? cursor, int? limit)
    {
        AssetClass? assetClass = null;
        if (@class is not null)
        {
            if (!AssetKey.TryParseClass(@class, out var parsedClass))
            {
                return ApiProblems.InvalidParameter("class", "Use one of: fiat, crypto, stock, etf.");
            }

            assetClass = parsedClass;
        }

        if (search?.Length > MaxSearchLength)
        {
            return ApiProblems.InvalidParameter("search", $"Search text is limited to {MaxSearchLength} characters.");
        }

        if (cursor is not null && !AssetKey.TryParse(cursor, out _))
        {
            return ApiProblems.InvalidParameter("cursor", "Pass back the nextCursor of the previous page.");
        }

        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize)
        {
            return ApiProblems.InvalidParameter("limit", $"Use a limit from 1 to {MaxPageSize}.");
        }

        var matches = catalog.ActiveOf(assetClass)
            .Where(entry => search is null
                || entry.Key.Symbol.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(entry => (Text: entry.Key.ToString(), Entry: entry))
            .Where(match => cursor is null || string.CompareOrdinal(match.Text, cursor) > 0)
            .OrderBy(match => match.Text, StringComparer.Ordinal)
            .Take(pageSize + 1)
            .ToList();

        var page = matches.Take(pageSize).Select(match => ToResponse(match.Entry)).ToList();
        var nextCursor = matches.Count > pageSize ? page[^1].Key.ToString() : null;
        return TypedResults.Ok(new AssetPage(page, nextCursor));
    }

    private static Results<Ok<AssetDetailResponse>, ProblemHttpResult> GetAsset(AssetCatalog catalog, QuoteReader quotes, string key, string? @in)
    {
        if (!AssetKey.TryParse(key, out var asset))
        {
            return ApiProblems.InvalidParameter("key", "Use an asset key such as crypto:BTC.");
        }

        if (!TryParseDisplayCurrency(catalog, @in, out var displayIn, out var invalidIn))
        {
            return invalidIn;
        }

        if (!catalog.TryGet(asset, out var entry))
        {
            return ApiProblems.UnknownAsset(asset);
        }

        return TypedResults.Ok(new AssetDetailResponse(ToResponse(entry), quotes.TryRead(asset, displayIn)));
    }

    /// <summary>Latest quotes for up to 50 assets in one display currency; assets without a price yet are left out.</summary>
    private static Results<Ok<List<QuoteResponse>>, ProblemHttpResult> GetQuotes(AssetCatalog catalog, QuoteReader quotes, string? keys, string? @in)
    {
        var requested = (keys ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Length is 0 or > MaxKeysPerRequest)
        {
            return ApiProblems.InvalidParameter("keys", $"Pass 1 to {MaxKeysPerRequest} comma-separated asset keys.");
        }

        if (!TryParseDisplayCurrency(catalog, @in, out var displayIn, out var invalidIn))
        {
            return invalidIn;
        }

        var assets = new List<AssetKey>();
        foreach (var text in requested.Distinct(StringComparer.Ordinal))
        {
            if (!AssetKey.TryParse(text, out var asset))
            {
                return ApiProblems.InvalidParameter("keys", $"'{text}' is not an asset key such as crypto:BTC.");
            }

            if (!catalog.TryGet(asset, out _))
            {
                return ApiProblems.UnknownAsset(asset);
            }

            assets.Add(asset);
        }

        var priced = assets.Select(asset => quotes.TryRead(asset, displayIn)).OfType<QuoteResponse>().ToList();
        return TypedResults.Ok(priced);
    }

    /// <summary>Reads <c>in=</c>, defaulting to US dollars; any tracked asset can be a display currency.</summary>
    public static bool TryParseDisplayCurrency(AssetCatalog catalog, string? text, out AssetKey displayIn, [NotNullWhen(false)] out ProblemHttpResult? problem)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        problem = null;
        displayIn = AssetKey.UsDollar;
        if (text is null)
        {
            return true;
        }

        if (!AssetKey.TryParse(text, out displayIn))
        {
            problem = ApiProblems.InvalidParameter("in", "Use an asset key such as fiat:EUR.");
            return false;
        }

        if (displayIn != AssetKey.UsDollar && !catalog.TryGet(displayIn, out _))
        {
            problem = ApiProblems.UnknownAsset(displayIn);
            return false;
        }

        return true;
    }

    private static AssetResponse ToResponse(CatalogEntry entry) =>
        new(entry.Key, entry.Key.Class, entry.Key.Symbol, entry.Name, entry.LogoUrl, entry.Tracks);
}
