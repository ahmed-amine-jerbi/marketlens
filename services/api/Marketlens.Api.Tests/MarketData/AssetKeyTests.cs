using Marketlens.Api.MarketData;

namespace Marketlens.Api.Tests.MarketData;

public sealed class AssetKeyTests
{
    [Theory]
    [InlineData("fiat:EUR", AssetClass.Fiat, "EUR")]
    [InlineData("crypto:BTC", AssetClass.Crypto, "BTC")]
    [InlineData("stock:BRK.B", AssetClass.Stock, "BRK.B")]
    [InlineData("stock:BF-B", AssetClass.Stock, "BF-B")]
    [InlineData("etf:SPY", AssetClass.Etf, "SPY")]
    [InlineData("crypto:1INCH", AssetClass.Crypto, "1INCH")]
    public void TryParse_reads_class_and_symbol(string text, AssetClass expectedClass, string expectedSymbol)
    {
        var isParsed = AssetKey.TryParse(text, out var key);

        Assert.True(isParsed);
        Assert.Equal(new AssetKey(expectedClass, expectedSymbol), key);
        Assert.Equal(text, key.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BTC")]
    [InlineData("crypto:btc")]
    [InlineData("Crypto:BTC")]
    [InlineData("bond:US10Y")]
    [InlineData("crypto:")]
    [InlineData("crypto:BTC:USD")]
    [InlineData("crypto:.BTC")]
    [InlineData("stock:ABCDEFGHIJKLMNOP")]
    [InlineData("fiat:EUR ")]
    public void TryParse_rejects_text_that_is_not_an_asset_key(string? text)
    {
        var isParsed = AssetKey.TryParse(text, out _);

        Assert.False(isParsed);
    }

    [Fact]
    public void Parse_throws_a_format_error_naming_the_bad_input()
    {
        var error = Assert.Throws<FormatException>(() => AssetKey.Parse("bitcoin"));

        Assert.Contains("'bitcoin'", error.Message, StringComparison.Ordinal);
    }
}
