using System.Globalization;
using System.Text.Json;

namespace Marketlens.Api.MarketData;

public sealed class FinnhubOptions
{
    public const string Section = "MarketData:Finnhub";

    /// <summary>From <c>FINNHUB_API_KEY</c>. Without it, stocks and ETFs use the sample feed.</summary>
    public string? ApiKey { get; set; }

    public Uri BaseUrl { get; set; } = new("https://finnhub.io/api/v1/");

    public Uri StreamUrl { get; set; } = new("wss://ws.finnhub.io");

    /// <summary>
    /// Time to poll every stock and ETF once. 50 symbols every 2 minutes is 25 calls a minute,
    /// inside the free 60/min with room for news.
    /// </summary>
    public TimeSpan QuotePollCycle { get; set; } = TimeSpan.FromMinutes(2);

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>A stock's latest trade and previous close from Finnhub's quote endpoint.</summary>
public sealed record StockQuote(AssetKey Asset, decimal PriceUsd, decimal? PreviousCloseUsd, DateTime TradedAtUtc);

/// <summary>Adapter for Finnhub REST (quotes). The key travels in a header, never in a URL that could be logged.</summary>
public sealed class FinnhubClient(HttpClient http)
{
    public const string SourceName = "finnhub";
    public const string ApiKeyHeader = "X-Finnhub-Token";

    /// <returns>Null when Finnhub doesn't know the symbol (it answers with zeros).</returns>
    public async Task<StockQuote?> FetchQuoteAsync(AssetKey asset, string finnhubSymbol, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync($"quote?symbol={Uri.EscapeDataString(finnhubSymbol)}", cancellationToken);
        ProviderRateLimitedException.ThrowIfLimited(response, SourceName);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var quote = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return ParseQuote(asset, quote.RootElement);
    }

    // {"c": last, "pc": previous close, "t": last trade, unix seconds, ...}
    public static StockQuote? ParseQuote(AssetKey asset, JsonElement quote)
    {
        var lastPrice = quote.GetProperty("c").GetDecimal();
        var tradedAtSeconds = quote.GetProperty("t").GetInt64();
        if (lastPrice <= 0 || tradedAtSeconds <= 0)
        {
            return null;
        }

        var previousClose = quote.TryGetProperty("pc", out var pc) && pc.GetDecimal() > 0 ? pc.GetDecimal() : (decimal?)null;
        return new StockQuote(asset, lastPrice, previousClose, DateTime.UnixEpoch.AddSeconds(tradedAtSeconds));
    }

    /// <summary>
    /// Reads a WebSocket message (<c>{"type": "trade", "data": [{"s", "p", "t", "v"}, ...]}</c>) into one
    /// tick per symbol, the last trade in the message. Pings and unknown symbols yield nothing.
    /// Prices are read from the JSON number text, so they never pass through a binary float.
    /// </summary>
    public static IReadOnlyList<PriceTick> ParseTradeMessage(ReadOnlySpan<byte> utf8Message, IReadOnlyDictionary<string, AssetKey> assetBySymbol)
    {
        ArgumentNullException.ThrowIfNull(assetBySymbol);

        JsonDocument message;
        try
        {
            var reader = new Utf8JsonReader(utf8Message);
            message = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return [];
        }

        using (message)
        {
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.GetString() != "trade"
                || !root.TryGetProperty("data", out var trades) || trades.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var latestBySymbol = new Dictionary<AssetKey, PriceTick>();
            foreach (var trade in trades.EnumerateArray())
            {
                if (trade.ValueKind != JsonValueKind.Object
                    || !trade.TryGetProperty("s", out var symbolField) || symbolField.ValueKind != JsonValueKind.String
                    || symbolField.GetString() is not { } symbol
                    || !assetBySymbol.TryGetValue(symbol, out var asset)
                    || !trade.TryGetProperty("p", out var priceField) || priceField.ValueKind != JsonValueKind.Number
                    || !priceField.TryGetDecimal(out var price) || price <= 0
                    || !trade.TryGetProperty("t", out var timeField) || timeField.ValueKind != JsonValueKind.Number
                    || !timeField.TryGetInt64(out var tradedAtMs))
                {
                    continue;
                }

                var tradedAtUtc = DateTime.UnixEpoch.AddMilliseconds(tradedAtMs);
                if (latestBySymbol.TryGetValue(asset, out var seen) && seen.ObservedAtUtc >= tradedAtUtc)
                {
                    continue;
                }

                latestBySymbol[asset] = new PriceTick(asset, price, ReferencePriceUsd: null, tradedAtUtc, SourceName, Freshness.Live);
            }

            return latestBySymbol.Values.ToList();
        }
    }

    public static string SubscribeMessage(string symbol) =>
        string.Create(CultureInfo.InvariantCulture, $$"""{"type":"subscribe","symbol":"{{symbol}}"}""");
}
