using System.Globalization;
using System.Text.Json;

namespace Marketlens.Api.MarketData;

public sealed class FrankfurterOptions
{
    public const string Section = "MarketData:Frankfurter";

    public Uri BaseUrl { get; set; } = new("https://api.frankfurter.dev/");

    /// <summary>Rates change once a day; polling hourly picks them up soon after publication.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(1);

    public int HistoryYears { get; set; } = 5;
}

/// <summary>A fiat currency's USD price on one day.</summary>
public sealed record DailyUsdPrice(AssetKey Asset, DateOnly Date, decimal PriceUsd);

/// <summary>
/// Adapter for Frankfurter v2 (fiat reference rates, no key). The only class that knows its JSON.
/// v2 rather than v1 because v1 serves only the ~30 ECB currencies and lacks TND (D27).
/// </summary>
public sealed class FrankfurterClient(HttpClient http)
{
    public const string SourceName = "frankfurter";

    private static readonly JsonSerializerOptions ProviderJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed record CurrencyDto(string IsoCode, string Name, string Type);

    private sealed record RateDto(DateOnly Date, string Base, string Quote, decimal Rate);

    /// <summary>Fiat currencies only; Frankfurter also lists coins and metals, which come from elsewhere.</summary>
    public async Task<IReadOnlyList<FiatCurrency>> FetchCurrenciesAsync(CancellationToken cancellationToken)
    {
        var currencies = await http.GetFromJsonAsync<List<CurrencyDto>>("v2/currencies", ProviderJson, cancellationToken)
            ?? throw new JsonException("Frankfurter returned an empty currency list.");

        return currencies
            .Where(currency => currency.Type == "currency" && AssetKey.TryParse($"fiat:{currency.IsoCode}", out _))
            .Select(currency => new FiatCurrency(currency.IsoCode, currency.Name))
            .ToList();
    }

    /// <summary>
    /// USD prices of the given currencies for every published day in the range. Frankfurter quotes
    /// "1 USD = rate × currency", so the USD price of the currency is 1 / rate.
    /// </summary>
    public async Task<IReadOnlyList<DailyUsdPrice>> FetchUsdPricesAsync(
        IReadOnlySet<string> currencyCodes,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"v2/rates?base=USD&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        var rates = await http.GetFromJsonAsync<List<RateDto>>(path, ProviderJson, cancellationToken)
            ?? throw new JsonException("Frankfurter returned an empty rate list.");

        return rates
            .Where(rate => rate.Base == "USD" && rate.Rate > 0 && currencyCodes.Contains(rate.Quote))
            .Select(rate => new DailyUsdPrice(
                new AssetKey(AssetClass.Fiat, rate.Quote),
                rate.Date,
                decimal.Round(1m / rate.Rate, 18, MidpointRounding.ToEven)))
            .ToList();
    }
}
