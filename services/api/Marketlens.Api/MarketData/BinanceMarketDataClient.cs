using System.Globalization;
using System.Net;
using System.Text.Json;

using Marketlens.Api.Data;

namespace Marketlens.Api.MarketData;

public sealed class BinanceOptions
{
    public const string Section = "MarketData:Binance";

    /// <summary>Keyless market-data host. <c>api.binance.com</c> refuses US IP addresses; override per deployment.</summary>
    public Uri MarketDataBaseUrl { get; set; } = new("https://data-api.binance.vision/");

    public Uri StreamBaseUrl { get; set; } = new("wss://data-stream.binance.vision/");

    /// <summary>How often the backfill re-checks for gaps (after downtime or a dropped stream).</summary>
    public TimeSpan GapFillInterval { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>
/// Adapter for Binance public market data over REST (klines). Prices are quoted in USDT, which
/// Marketlens treats as 1 USD (D26).
/// </summary>
public sealed class BinanceMarketDataClient(HttpClient http)
{
    public const string SourceName = "binance";

    /// <summary>Binance returns at most 1000 klines per request.</summary>
    public const int MaxKlinesPerRequest = 1000;

    public static TimeSpan Length(CandleInterval interval) => interval switch
    {
        CandleInterval.OneMinute => TimeSpan.FromMinutes(1),
        CandleInterval.OneHour => TimeSpan.FromHours(1),
        CandleInterval.OneDay => TimeSpan.FromDays(1),
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, null),
    };

    /// <summary>Candles whose bucket starts in [fromUtc, toUtc), fetched page by page.</summary>
    public async Task<IReadOnlyList<CandleRow>> FetchCandlesAsync(
        AssetKey asset,
        string binanceSymbol,
        CandleInterval interval,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken)
    {
        var candles = new List<CandleRow>();
        var pageStartUtc = fromUtc;
        while (pageStartUtc < toUtc)
        {
            var page = await FetchPageAsync(asset, binanceSymbol, interval, pageStartUtc, toUtc, cancellationToken);
            candles.AddRange(page);
            if (page.Count < MaxKlinesPerRequest)
            {
                break;
            }

            pageStartUtc = page[^1].BucketStartUtc + Length(interval);
        }

        return candles;
    }

    private async Task<List<CandleRow>> FetchPageAsync(
        AssetKey asset,
        string binanceSymbol,
        CandleInterval interval,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken)
    {
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"api/v3/klines?symbol={binanceSymbol}&interval={PriceCandleConfiguration.ToColumnValue(interval)}&startTime={ToUnixMs(fromUtc)}&endTime={ToUnixMs(toUtc) - 1}&limit={MaxKlinesPerRequest}");

        using var response = await http.GetAsync(path, cancellationToken);
        ProviderRateLimitedException.ThrowIfLimited(response, SourceName);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var klines = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

        return klines.RootElement.EnumerateArray().Select(kline => ToCandle(asset, interval, kline)).ToList();
    }

    // A kline is [openTime, open, high, low, close, volume, closeTime, ...] with prices as decimal strings.
    private static CandleRow ToCandle(AssetKey asset, CandleInterval interval, JsonElement kline) => new(
        asset,
        interval,
        DateTime.UnixEpoch.AddMilliseconds(kline[0].GetInt64()),
        ParseDecimal(kline[1]),
        ParseDecimal(kline[2]),
        ParseDecimal(kline[3]),
        ParseDecimal(kline[4]),
        ParseDecimal(kline[5]),
        SourceName);

    private static decimal ParseDecimal(JsonElement value) =>
        decimal.Parse(value.GetString() ?? throw new JsonException("Binance kline field is null."), NumberStyles.Number, CultureInfo.InvariantCulture);

    private static long ToUnixMs(DateTime utc) => (long)(DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalMilliseconds;
}

/// <summary>A provider refused a request for exceeding its rate limit; wait at least <see cref="RetryAfter"/>.</summary>
public sealed class ProviderRateLimitedException(string providerName, TimeSpan? retryAfter)
    : HttpRequestException($"{providerName} rate limit reached; retry after {retryAfter?.ToString() ?? "a backoff delay"}.")
{
    public string ProviderName { get; } = providerName;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public static void ThrowIfLimited(HttpResponseMessage response, string providerName)
    {
        ArgumentNullException.ThrowIfNull(response);

        // Binance answers 429 when limited and 418 once it has banned the IP for ignoring 429s.
        if (response.StatusCode is not (HttpStatusCode.TooManyRequests or (HttpStatusCode)418))
        {
            return;
        }

        throw new ProviderRateLimitedException(providerName, response.Headers.RetryAfter?.Delta);
    }
}
