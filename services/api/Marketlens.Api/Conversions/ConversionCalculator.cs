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
    /// <summary>
    /// Significant digits kept on rates and converted amounts. Providers publish 5–10; the rest of a
    /// ratio of two USD prices is rounding residue (a daily fiat rate stored as 1/rate), which would
    /// otherwise show up as noise such as 227706.6842400000000715.
    /// </summary>
    public const int SignificantDigits = 15;

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
        return RoundToSignificantDigits(amount * conversion.Rate);
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

    public static decimal RoundToSignificantDigits(decimal value)
    {
        if (value == 0)
        {
            return 0;
        }

        // decimal keeps at most 28 places; the integer digits count toward the significant ones.
        var integerDigits = (int)Math.Floor(Math.Log10((double)Math.Abs(value))) + 1;
        var decimalPlaces = Math.Clamp(SignificantDigits - integerDigits, 0, 28);
        return decimal.Round(value, decimalPlaces, MidpointRounding.ToEven);
    }

    private static decimal Ratio(decimal numeratorUsd, decimal denominatorUsd) =>
        RoundToSignificantDigits(numeratorUsd / denominatorUsd);

    private static void EnsurePositive(UsdQuote leg)
    {
        if (leg.PriceUsd <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(leg), leg.PriceUsd, $"USD price of {leg.Asset} must be positive.");
        }
    }
}
