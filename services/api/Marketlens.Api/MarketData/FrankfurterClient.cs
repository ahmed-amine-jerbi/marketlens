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

/// <summary>A fiat currency's (or Tether's) USD price on one day.</summary>
public sealed record DailyUsdPrice(AssetKey Asset, DateOnly Date, decimal PriceUsd);

/// <summary>
/// Adapter for Frankfurter v2 (reference rates, no key). The only class that knows its JSON.
/// v2 rather than v1 because v1 serves only the ~30 ECB currencies and lacks TND (D27). Besides
/// fiat, its USDT row prices Tether, which is the USDT rate every Binance price is converted at.
/// </summary>
public sealed partial class FrankfurterClient(HttpClient http, ILogger<FrankfurterClient> logger)
{
    public const string SourceName = "frankfurter";

    /// <summary>
    /// A USDT price outside this band is a provider error, not a de-peg to show: it would reprice
    /// every coin, so it is refused and the last accepted rate stays in use.
    /// </summary>
    public const decimal MinTetherPriceUsd = 0.5m;

    public const decimal MaxTetherPriceUsd = 1.5m;

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
    /// USD prices of the given codes (fiat currencies, and <c>USDT</c> for Tether) for every
    /// published day in the range. Frankfurter quotes "1 USD = rate × currency", so the USD price of
    /// the currency is 1 / rate. A Tether price outside the accepted band is left out and logged.
    /// </summary>
    public async Task<IReadOnlyList<DailyUsdPrice>> FetchUsdPricesAsync(
        IReadOnlySet<string> quoteCodes,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"v2/rates?base=USD&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        var rates = await http.GetFromJsonAsync<List<RateDto>>(path, ProviderJson, cancellationToken)
            ?? throw new JsonException("Frankfurter returned an empty rate list.");

        return rates
            .Where(rate => rate.Base == "USD" && rate.Rate > 0 && quoteCodes.Contains(rate.Quote))
            .Select(rate => new DailyUsdPrice(
                rate.Quote == AssetKey.Tether.Symbol ? AssetKey.Tether : new AssetKey(AssetClass.Fiat, rate.Quote),
                rate.Date,
                decimal.Round(1m / rate.Rate, 18, MidpointRounding.ToEven)))
            .Where(IsPlausible)
            .ToList();
    }

    private bool IsPlausible(DailyUsdPrice price)
    {
        if (price.Asset != AssetKey.Tether || price.PriceUsd is >= MinTetherPriceUsd and <= MaxTetherPriceUsd)
        {
            return true;
        }

        LogTetherPriceRefused(logger, price.Date, price.PriceUsd);
        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Frankfurter's USDT price for {Date} is {PriceUsd} USD, outside the accepted band; refused")]
    private static partial void LogTetherPriceRefused(ILogger logger, DateOnly date, decimal priceUsd);
}
