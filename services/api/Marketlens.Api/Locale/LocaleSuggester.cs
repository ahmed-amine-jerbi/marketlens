using System.Collections.Frozen;
using System.Globalization;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;

namespace Marketlens.Api.Locale;

public sealed record LocaleSuggestion(string Language, string DisplayCurrency, string Reason);

/// <summary>
/// Suggests a language and a display currency for a visitor who hasn't chosen (D22): the first
/// supported language in the browser's Accept-Language, and the currency of the first region named
/// there (fr-TN → TND). Clients apply it only when the user has no saved choice; the choice always wins.
/// </summary>
public static class LocaleSuggester
{
    public const string DefaultLanguage = "en";
    public const string DefaultCurrency = "fiat:USD";

    private static readonly FrozenSet<string> SupportedLanguages = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "en", "fr", "de");

    /// <summary>Euro-area and other common countries; anything else keeps US dollars.</summary>
    private static readonly FrozenDictionary<string, string> CurrencyByCountry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["AT"] = "EUR",
        ["BE"] = "EUR",
        ["DE"] = "EUR",
        ["ES"] = "EUR",
        ["FI"] = "EUR",
        ["FR"] = "EUR",
        ["IE"] = "EUR",
        ["IT"] = "EUR",
        ["LU"] = "EUR",
        ["NL"] = "EUR",
        ["PT"] = "EUR",
        ["CH"] = "CHF",
        ["LI"] = "CHF",
        ["GB"] = "GBP",
        ["TN"] = "TND",
        ["DZ"] = "DZD",
        ["MA"] = "MAD",
        ["US"] = "USD",
        ["CA"] = "CAD",
        ["JP"] = "JPY",
        ["AU"] = "AUD",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static RouteGroupBuilder MapLocaleEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/locale-suggestion", (HttpRequest request) => Suggest(request.Headers.AcceptLanguage.ToString()))
            .WithName("GetLocaleSuggestion").WithTags("Locale");
        return api;
    }

    public static Ok<LocaleSuggestion> Suggest(string? acceptLanguage)
    {
        var preferred = ParsePreferredTags(acceptLanguage);
        var language = preferred.Select(PrimaryLanguage).FirstOrDefault(SupportedLanguages.Contains);
        var region = preferred.Select(Region).FirstOrDefault(code => code is not null && CurrencyByCountry.ContainsKey(code));

        var displayCurrency = region is null ? DefaultCurrency : $"fiat:{CurrencyByCountry[region]}";
        var reason = language is null && region is null ? "default" : "browser-language";
        return TypedResults.Ok(new LocaleSuggestion((language ?? DefaultLanguage).ToLowerInvariant(), displayCurrency, reason));
    }

    private static List<string> ParsePreferredTags(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage)
            || !StringWithQualityHeaderValue.TryParseList(acceptLanguage.Split(','), out var tags))
        {
            return [];
        }

        return tags
            .Where(tag => tag.Quality is null or > 0)
            .OrderByDescending(tag => tag.Quality ?? 1)
            .Select(tag => tag.Value.ToString())
            .Where(tag => tag.Length is > 1 and < 36)
            .ToList();
    }

    private static string PrimaryLanguage(string tag) => tag.Split('-')[0];

    private static string? Region(string tag)
    {
        var parts = tag.Split('-');
        return parts.Skip(1).FirstOrDefault(part => part.Length == 2 && part.All(char.IsAsciiLetter))?.ToUpper(CultureInfo.InvariantCulture);
    }
}
