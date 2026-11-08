using Marketlens.Api.MarketData;

namespace Marketlens.Api.Conversions;

/// <summary>The price of one asset expressed in another, built from the two USD legs.</summary>
/// <param name="Rate">How many units of <paramref name="To"/> one unit of <paramref name="From"/> is worth.</param>
/// <param name="ChangePct">Change of the rate against its reference, in percent; null without both references.</param>
/// <param name="AsOfUtc">The older leg's time: a pair is only as current as its slowest side.</param>
/// <param name="Freshness">The slower leg's freshness.</param>
public sealed record Conversion(
    AssetKey From,
    AssetKey To,
    decimal Rate,
    decimal? ChangePct,
    DateTime AsOfUtc,
    Freshness Freshness,
    UsdQuote FromLeg,
    UsdQuote ToLeg);

/// <summary>
/// The only place a cross rate is computed (D36). Every asset has a USD price, so any pair is the
/// ratio of two USD prices (D26). Pure: no I/O, no clock.
/// </summary>
public static class ConversionCalculator
{
    /// <summary>Decimal places kept on rates; matches the numeric(38,18) storage of prices.</summary>
    public const int RateDecimalPlaces = 18;

    private const int ChangePctDecimalPlaces = 4;

    public static Conversion Convert(UsdQuote from, UsdQuote to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        EnsurePositive(from);
        EnsurePositive(to);

        var rate = Ratio(from.PriceUsd, to.PriceUsd);
        var changePct = CalculateChangePct(from, to, rate);
        var asOfUtc = from.AsOfUtc <= to.AsOfUtc ? from.AsOfUtc : to.AsOfUtc;
        var freshness = from.Freshness >= to.Freshness ? from.Freshness : to.Freshness;

        return new Conversion(from.Asset, to.Asset, rate, changePct, asOfUtc, freshness, from, to);
    }

    public static decimal ConvertAmount(decimal amount, Conversion conversion)
    {
        ArgumentNullException.ThrowIfNull(conversion);
        return decimal.Round(amount * conversion.Rate, RateDecimalPlaces, MidpointRounding.ToEven);
    }

    private static decimal? CalculateChangePct(UsdQuote from, UsdQuote to, decimal rate)
    {
        if (from.ReferencePriceUsd is not > 0m || to.ReferencePriceUsd is not > 0m)
        {
            return null;
        }

        var referenceRate = Ratio(from.ReferencePriceUsd.Value, to.ReferencePriceUsd.Value);
        return decimal.Round((rate / referenceRate - 1m) * 100m, ChangePctDecimalPlaces, MidpointRounding.ToEven);
    }

    private static decimal Ratio(decimal numeratorUsd, decimal denominatorUsd) =>
        decimal.Round(numeratorUsd / denominatorUsd, RateDecimalPlaces, MidpointRounding.ToEven);

    private static void EnsurePositive(UsdQuote leg)
    {
        if (leg.PriceUsd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leg), leg.PriceUsd, $"USD price of {leg.Asset} must be positive.");
        }
    }
}
