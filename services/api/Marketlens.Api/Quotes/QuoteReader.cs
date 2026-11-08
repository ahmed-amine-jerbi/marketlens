using System.Text.Json.Serialization;

using Marketlens.Api.Conversions;
using Marketlens.Api.MarketData;

namespace Marketlens.Api.Quotes;

/// <summary>What a quote's change is measured against (D40).</summary>
public enum ChangeBasis
{
    [JsonStringEnumMemberName("previous-close")]
    PreviousClose,

    [JsonStringEnumMemberName("rolling-24h")]
    Rolling24h,
}

/// <summary>An asset's price expressed in a display currency (<paramref name="In"/>), as clients receive it.</summary>
public sealed record QuoteResponse(
    AssetKey Key,
    decimal Price,
    AssetKey In,
    decimal? ChangePct,
    ChangeBasis ChangeBasis,
    DateTime AsOfUtc,
    string Source,
    Freshness Freshness);

/// <summary>
/// Turns quote-book prices into quotes in any display currency (D36), for REST and the live hub
/// alike. The conversion itself is <see cref="ConversionCalculator"/>'s; this only shapes the answer.
/// </summary>
public sealed class QuoteReader(QuoteBook quoteBook)
{
    public static ChangeBasis BasisFor(AssetClass assetClass) =>
        assetClass == AssetClass.Crypto ? ChangeBasis.Rolling24h : ChangeBasis.PreviousClose;

    /// <returns>Null when the asset or the display currency has no price yet.</returns>
    public QuoteResponse? TryRead(AssetKey asset, AssetKey displayIn)
    {
        if (!quoteBook.TryGet(asset, out var assetQuote) || !quoteBook.TryGet(displayIn, out var displayQuote))
        {
            return null;
        }

        return FromUsd(assetQuote, displayQuote);
    }

    public static QuoteResponse FromUsd(UsdQuote assetQuote, UsdQuote displayQuote)
    {
        ArgumentNullException.ThrowIfNull(assetQuote);
        ArgumentNullException.ThrowIfNull(displayQuote);

        var conversion = ConversionCalculator.Convert(assetQuote, displayQuote);
        return new QuoteResponse(
            assetQuote.Asset,
            conversion.Rate,
            displayQuote.Asset,
            conversion.ChangePct,
            BasisFor(assetQuote.Asset.Class),
            conversion.AsOfUtc,
            assetQuote.Source,
            conversion.Freshness);
    }
}
