using System.Globalization;

using Marketlens.Api.Conversions;
using Marketlens.Api.MarketData;

namespace Marketlens.Api.Tests.Conversions;

public sealed class ConversionCalculatorTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 15, 0, 0, DateTimeKind.Utc);

    private static readonly UsdQuote Bitcoin = Quote("crypto:BTC", 76036.00m, reference: 75873.47m, Now, Freshness.Live);
    private static readonly UsdQuote Ether = Quote("crypto:ETH", 2238.50m, reference: 2241.39m, Now, Freshness.Live);
    private static readonly UsdQuote Euro = Quote("fiat:EUR", 1m / 0.88296m, reference: 1m / 0.88597m, Now.Date, Freshness.Daily);
    private static readonly UsdQuote TunisianDinar = Quote("fiat:TND", 1m / 2.9809m, reference: 1m / 2.9811m, Now.Date.AddDays(-1), Freshness.Daily);
    private static readonly UsdQuote UsDollar = Quote("fiat:USD", 1m, reference: 1m, Now, Freshness.Live);

    [Fact]
    public void Convert_crypto_to_fiat_divides_the_two_usd_prices()
    {
        var conversion = ConversionCalculator.Convert(Bitcoin, TunisianDinar);

        Assert.Equal(76036.00m * 2.9809m, conversion.Rate, precision: 10);
        Assert.Equal(AssetKey.Parse("crypto:BTC"), conversion.From);
        Assert.Equal(AssetKey.Parse("fiat:TND"), conversion.To);
    }

    [Fact]
    public void Convert_fiat_to_fiat_crosses_through_usd()
    {
        var conversion = ConversionCalculator.Convert(Euro, TunisianDinar);

        Assert.Equal(2.9809m / 0.88296m, conversion.Rate, precision: 10);
    }

    [Fact]
    public void Convert_crypto_to_crypto_gives_the_pair_rate()
    {
        var conversion = ConversionCalculator.Convert(Ether, Bitcoin);

        Assert.Equal(2238.50m / 76036.00m, conversion.Rate, precision: 15);
    }

    [Fact]
    public void Convert_an_asset_to_itself_is_exactly_one_with_no_change()
    {
        var conversion = ConversionCalculator.Convert(Bitcoin, Bitcoin);

        Assert.Equal(1m, conversion.Rate);
        Assert.Equal(0m, conversion.ChangePct);
    }

    [Fact]
    public void Convert_into_usd_keeps_the_usd_price_and_its_change()
    {
        var conversion = ConversionCalculator.Convert(Bitcoin, UsDollar);

        Assert.Equal(76036.00m, conversion.Rate);
        Assert.Equal(decimal.Round((76036.00m / 75873.47m - 1m) * 100m, 4), conversion.ChangePct);
    }

    [Fact]
    public void Convert_is_only_as_current_and_as_fresh_as_its_slower_leg()
    {
        var conversion = ConversionCalculator.Convert(Bitcoin, TunisianDinar);

        Assert.Equal(TunisianDinar.AsOfUtc, conversion.AsOfUtc);
        Assert.Equal(Freshness.Daily, conversion.Freshness);
    }

    [Fact]
    public void Convert_reports_no_change_when_a_leg_has_no_reference_price()
    {
        var bitcoinWithoutReference = Bitcoin with { ReferencePriceUsd = null };

        var conversion = ConversionCalculator.Convert(bitcoinWithoutReference, Euro);

        Assert.Null(conversion.ChangePct);
    }

    [Fact]
    public void Convert_keeps_tiny_rates_without_rounding_them_to_zero()
    {
        var shibaInu = Quote("crypto:SHIB", 0.00001234m, reference: null, Now, Freshness.Live);

        var conversion = ConversionCalculator.Convert(shibaInu, Bitcoin);

        Assert.True(conversion.Rate > 0m);
        Assert.Equal(0.00001234m / 76036.00m, conversion.Rate, precision: 18);
    }

    [Fact]
    public void Convert_drops_the_rounding_residue_of_inverted_daily_rates()
    {
        var dinarFromStoredRate = TunisianDinar with { PriceUsd = decimal.Round(1m / 2.9809m, 18) };

        var conversion = ConversionCalculator.Convert(Bitcoin, dinarFromStoredRate);

        Assert.Equal(226_655.7124m, conversion.Rate);
    }

    [Theory]
    [InlineData("227706.6842400000000715", "227706.684240000")]
    [InlineData("0.000000000162291546", "0.000000000162291546")]
    [InlineData("1234567890123456789", "1234567890123456789")]
    [InlineData("0.1234567890123456789", "0.123456789012346")]
    public void RoundToSignificantDigits_keeps_fifteen_significant_digits(string value, string expected)
    {
        var rounded = ConversionCalculator.RoundToSignificantDigits(decimal.Parse(value, CultureInfo.InvariantCulture));

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), rounded);
    }

    [Fact]
    public void Convert_rejects_a_non_positive_price()
    {
        var brokenQuote = Bitcoin with { PriceUsd = 0m };

        Assert.Throws<ArgumentOutOfRangeException>(() => ConversionCalculator.Convert(brokenQuote, Euro));
    }

    [Fact]
    public void TryConvertAmount_multiplies_the_amount_by_the_rate()
    {
        var conversion = ConversionCalculator.Convert(Bitcoin, TunisianDinar);

        var isConverted = ConversionCalculator.TryConvertAmount(0.25m, conversion, out var convertedAmount);

        Assert.True(isConverted);
        Assert.Equal(0.25m * conversion.Rate, convertedAmount);
    }

    [Fact]
    public void TryConvertAmount_reports_a_result_too_large_for_a_decimal()
    {
        var almostWorthless = Quote("fiat:ZZZ", 0.000000000001m, reference: null, Now, Freshness.Daily);
        var conversion = ConversionCalculator.Convert(Bitcoin, almostWorthless);

        var isConverted = ConversionCalculator.TryConvertAmount(1_000_000_000_000_000m, conversion, out _);

        Assert.False(isConverted);
    }

    private static UsdQuote Quote(string key, decimal priceUsd, decimal? reference, DateTime asOfUtc, Freshness freshness) =>
        new(AssetKey.Parse(key), priceUsd, reference, asOfUtc, "test", freshness);
}
