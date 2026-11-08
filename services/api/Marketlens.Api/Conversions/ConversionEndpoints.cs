using System.Diagnostics.CodeAnalysis;

using Marketlens.Api.Http;
using Marketlens.Api.MarketData;

using Microsoft.AspNetCore.Http.HttpResults;

namespace Marketlens.Api.Conversions;

public sealed record ConversionLegResponse(AssetKey Key, decimal PriceUsd, DateTime AsOfUtc, string Source, Freshness Freshness);

public sealed record ConversionResponse(
    AssetKey From,
    AssetKey To,
    decimal Rate,
    decimal Amount,
    decimal ConvertedAmount,
    decimal? ChangePct,
    DateTime AsOfUtc,
    Freshness Freshness,
    IReadOnlyList<ConversionLegResponse> Legs);

/// <summary>
/// <c>GET /api/v1/conversions</c>: converts an amount between any two tracked assets. This feature,
/// from endpoint to <see cref="ConversionCalculator"/>, is the reference implementation (D41).
/// </summary>
public static class ConversionEndpoints
{
    /// <summary>Upper bound on amounts, so the converted value always fits a decimal.</summary>
    public const decimal MaxAmount = 1_000_000_000_000_000m;

    public static RouteGroupBuilder MapConversionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/conversions", Convert).WithName("Convert").WithTags("Conversions")
            .WithSummary("Convert an amount between any two tracked assets")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    private static Results<Ok<ConversionResponse>, ProblemHttpResult> Convert(AssetCatalog catalog, QuoteBook quoteBook, string? from, string? to, decimal? amount)
    {
        if (!TryParseTrackedAsset(catalog, "from", from, out var fromAsset, out var invalidFrom))
        {
            return invalidFrom;
        }

        if (!TryParseTrackedAsset(catalog, "to", to, out var toAsset, out var invalidTo))
        {
            return invalidTo;
        }

        var amountToConvert = amount ?? 1m;
        if (amountToConvert is <= 0 or > MaxAmount)
        {
            return ApiProblems.InvalidParameter("amount", $"Use an amount above 0 and up to {MaxAmount:N0}.");
        }

        if (!quoteBook.TryGet(fromAsset, out var fromLeg))
        {
            return ApiProblems.RateUnavailable(fromAsset);
        }

        if (!quoteBook.TryGet(toAsset, out var toLeg))
        {
            return ApiProblems.RateUnavailable(toAsset);
        }

        var conversion = ConversionCalculator.Convert(fromLeg, toLeg);
        if (!ConversionCalculator.TryConvertAmount(amountToConvert, conversion, out var convertedAmount))
        {
            return ApiProblems.InvalidParameter("amount", "The converted amount is too large to represent; use a smaller amount.");
        }

        return TypedResults.Ok(new ConversionResponse(
            conversion.From,
            conversion.To,
            conversion.Rate,
            amountToConvert,
            convertedAmount,
            conversion.ChangePct,
            conversion.AsOfUtc,
            conversion.Freshness,
            [ToLeg(conversion.FromLeg), ToLeg(conversion.ToLeg)]));
    }

    private static bool TryParseTrackedAsset(AssetCatalog catalog, string parameter, string? text, out AssetKey asset, [NotNullWhen(false)] out ProblemHttpResult? problem)
    {
        problem = null;
        if (!AssetKey.TryParse(text, out asset))
        {
            problem = ApiProblems.InvalidParameter(parameter, "Use an asset key such as crypto:BTC or fiat:TND.");
            return false;
        }

        if (asset != AssetKey.UsDollar && !catalog.TryGet(asset, out _))
        {
            problem = ApiProblems.UnknownAsset(asset);
            return false;
        }

        return true;
    }

    private static ConversionLegResponse ToLeg(UsdQuote leg) => new(leg.Asset, leg.PriceUsd, leg.AsOfUtc, leg.Source, leg.Freshness);
}
