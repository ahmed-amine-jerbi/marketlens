using Marketlens.Api.Http;
using Marketlens.Api.MarketData;
using Marketlens.Api.Quotes;

using Microsoft.AspNetCore.Http.HttpResults;

namespace Marketlens.Api.History;

/// <summary>Chart history: <c>/api/v1/assets/{key}/history</c> and <c>/api/v1/conversions/history</c>.</summary>
public static class HistoryEndpoints
{
    public static RouteGroupBuilder MapHistoryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/assets/{key}/history", GetAssetHistory).WithName("GetAssetHistory").WithTags("History")
            .WithSummary("Get an asset's price history for a chart range in a display currency")
            .ProducesProblem(StatusCodes.Status404NotFound);
        api.MapGet("/conversions/history", GetConversionHistory).WithName("GetConversionHistory").WithTags("History")
            .WithSummary("Get the rate between two assets over a chart range")
            .ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    private static async Task<Results<Ok<HistoryResponse>, ProblemHttpResult>> GetAssetHistory(
        AssetCatalog catalog,
        HistoryQuery history,
        string key,
        string? range,
        string? @in,
        CancellationToken cancellationToken)
    {
        if (!AssetKey.TryParse(key, out var asset))
        {
            return ApiProblems.InvalidParameter("key", "Use an asset key such as crypto:BTC.");
        }

        if (!catalog.TryGet(asset, out _))
        {
            return ApiProblems.UnknownAsset(asset);
        }

        return await ReadAsync(catalog, history, asset, range, @in, cancellationToken);
    }

    /// <summary>The rate between two assets over time: the history of <paramref name="from"/> shown in <paramref name="to"/>.</summary>
    private static async Task<Results<Ok<HistoryResponse>, ProblemHttpResult>> GetConversionHistory(
        AssetCatalog catalog,
        HistoryQuery history,
        string? from,
        string? to,
        string? range,
        CancellationToken cancellationToken)
    {
        if (!AssetKey.TryParse(from, out var fromAsset))
        {
            return ApiProblems.InvalidParameter("from", "Use an asset key such as crypto:BTC.");
        }

        if (fromAsset != AssetKey.UsDollar && !catalog.TryGet(fromAsset, out _))
        {
            return ApiProblems.UnknownAsset(fromAsset);
        }

        return await ReadAsync(catalog, history, fromAsset, range, to, cancellationToken);
    }

    private static async Task<Results<Ok<HistoryResponse>, ProblemHttpResult>> ReadAsync(
        AssetCatalog catalog,
        HistoryQuery history,
        AssetKey asset,
        string? rangeText,
        string? displayInText,
        CancellationToken cancellationToken)
    {
        if (!HistoryQuery.TryParseRange(rangeText ?? "1M", out var range))
        {
            return ApiProblems.InvalidParameter("range", "Use one of: 1D, 1W, 1M, 3M, 1Y, 5Y.");
        }

        if (!QuoteEndpoints.TryParseDisplayCurrency(catalog, displayInText, out var displayIn, out var invalidIn))
        {
            return invalidIn;
        }

        return TypedResults.Ok(await history.ReadAsync(asset, displayIn, range, cancellationToken));
    }
}
