using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Marketlens.Api.MarketData;

public enum AssetClass
{
    Fiat,
    Crypto,
    Stock,
    Etf,
}

/// <summary>
/// Stable identifier of an asset across the API, database and clients: the class, a colon and the
/// symbol, for example <c>fiat:EUR</c>, <c>crypto:BTC</c>, <c>stock:BRK.B</c>, <c>etf:SPY</c>.
/// </summary>
public readonly partial record struct AssetKey(AssetClass Class, string Symbol)
{
    public static readonly AssetKey UsDollar = new(AssetClass.Fiat, "USD");

    public static bool TryParse([NotNullWhen(true)] string? text, out AssetKey key)
    {
        key = default;
        if (text is null)
        {
            return false;
        }

        var match = KeyPattern().Match(text);
        if (!match.Success || !TryParseClass(match.Groups["class"].Value, out var assetClass))
        {
            return false;
        }

        key = new AssetKey(assetClass, match.Groups["symbol"].Value);
        return true;
    }

    public static AssetKey Parse(string text) =>
        TryParse(text, out var key) ? key : throw new FormatException($"'{text}' is not an asset key such as 'crypto:BTC'.");

    public static string ClassName(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Fiat => "fiat",
        AssetClass.Crypto => "crypto",
        AssetClass.Stock => "stock",
        AssetClass.Etf => "etf",
        _ => throw new ArgumentOutOfRangeException(nameof(assetClass), assetClass, null),
    };

    public static bool TryParseClass(string name, out AssetClass assetClass)
    {
        (var isKnown, assetClass) = name switch
        {
            "fiat" => (true, AssetClass.Fiat),
            "crypto" => (true, AssetClass.Crypto),
            "stock" => (true, AssetClass.Stock),
            "etf" => (true, AssetClass.Etf),
            _ => (false, default),
        };
        return isKnown;
    }

    public override string ToString() => $"{ClassName(Class)}:{Symbol}";

    // Symbols are upper-case; stock symbols may contain a dot or dash (BRK.B, BF-B).
    [GeneratedRegex("^(?<class>[a-z]+):(?<symbol>[A-Z0-9][A-Z0-9.\\-]{0,14})$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
