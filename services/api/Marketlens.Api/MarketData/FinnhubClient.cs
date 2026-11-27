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

/// <summary>
/// A company as Finnhub's free profile describes it. Money is in US dollars, or null when the company
/// reports in another currency; market cap and shares are whole units (Finnhub sends millions).
/// </summary>
public sealed record CompanyProfile(
    string? Industry,
    string? Country,
    string? Exchange,
    DateOnly? IpoDate,
    Uri? WebsiteUrl,
    Uri? LogoUrl,
    decimal? MarketCapUsd,
    decimal? SharesOutstanding);

/// <summary>Per-share figures and ratios from Finnhub's free basic financials; each may be missing.</summary>
public sealed record BasicFinancials(
    decimal? PeRatio,
    decimal? EpsTtmUsd,
    decimal? DividendYieldPct,
    decimal? High52WeekUsd,
    decimal? Low52WeekUsd,
    decimal? Beta);

/// <summary>How many analysts rated a stock strong buy … strong sell in one month, as Finnhub reports it.</summary>
public sealed record RecommendationTrend(DateOnly Period, int StrongBuy, int Buy, int Hold, int Sell, int StrongSell);

/// <summary>
/// Adapter for Finnhub REST: quotes, and the free fundamentals (profile, basic financials, analyst
/// recommendations, earnings calendar). The key travels in a header, never in a URL that could be logged.
/// </summary>
public sealed class FinnhubClient(HttpClient http)
{
    public const string SourceName = "finnhub";
    public const string ApiKeyHeader = "X-Finnhub-Token";

    /// <summary>Provider text is stored and shown; anything longer is cut, never trusted to be short.</summary>
    public const int MaxTextLength = 120;

    private const decimal Million = 1_000_000m;

    /// <summary>The only hosts a company logo may come from (they are also the only ones the web CSP allows).</summary>
    private static readonly HashSet<string> LogoHosts = new(StringComparer.OrdinalIgnoreCase) { "static.finnhub.io", "static2.finnhub.io" };

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

    public async Task<CompanyProfile?> FetchCompanyProfileAsync(string finnhubSymbol, CancellationToken cancellationToken)
    {
        using var profile = await GetJsonAsync($"stock/profile2?symbol={Uri.EscapeDataString(finnhubSymbol)}", cancellationToken);
        return ParseCompanyProfile(profile.RootElement);
    }

    public async Task<BasicFinancials> FetchBasicFinancialsAsync(string finnhubSymbol, CancellationToken cancellationToken)
    {
        using var financials = await GetJsonAsync($"stock/metric?symbol={Uri.EscapeDataString(finnhubSymbol)}&metric=all", cancellationToken);
        return ParseBasicFinancials(financials.RootElement);
    }

    public async Task<IReadOnlyList<RecommendationTrend>> FetchRecommendationTrendsAsync(string finnhubSymbol, CancellationToken cancellationToken)
    {
        using var trends = await GetJsonAsync($"stock/recommendation?symbol={Uri.EscapeDataString(finnhubSymbol)}", cancellationToken);
        return ParseRecommendationTrends(trends.RootElement);
    }

    /// <summary>The first earnings date from <paramref name="fromDate"/> within <paramref name="days"/>, or null when none is scheduled.</summary>
    public async Task<DateOnly?> FetchNextEarningsDateAsync(string finnhubSymbol, DateOnly fromDate, int days, CancellationToken cancellationToken)
    {
        var from = fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = fromDate.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var calendar = await GetJsonAsync($"calendar/earnings?symbol={Uri.EscapeDataString(finnhubSymbol)}&from={from}&to={to}", cancellationToken);
        return ParseNextEarningsDate(calendar.RootElement, fromDate);
    }

    /// <returns>Null when Finnhub doesn't know the symbol (it answers with an empty object).</returns>
    public static CompanyProfile? ParseCompanyProfile(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object || Text(profile, "ticker") is null)
        {
            return null;
        }

        var isUsd = string.Equals(Text(profile, "currency"), "USD", StringComparison.Ordinal);
        var ipoDate = DateOnly.TryParseExact(Text(profile, "ipo"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ipo) ? ipo : (DateOnly?)null;
        var logoUrl = HttpsUrl(profile, "logo") is { } logo && LogoHosts.Contains(logo.Host) ? logo : null;

        return new CompanyProfile(
            Industry: Text(profile, "finnhubIndustry"),
            Country: Text(profile, "country"),
            Exchange: Text(profile, "exchange"),
            IpoDate: ipoDate,
            WebsiteUrl: HttpsUrl(profile, "weburl"),
            LogoUrl: logoUrl,
            MarketCapUsd: isUsd ? Number(profile, "marketCapitalization") * Million : null,
            SharesOutstanding: Number(profile, "shareOutstanding") * Million);
    }

    // {"metric": {"peTTM": 39.17, "epsTTM": 8.97, "dividendYieldIndicatedAnnual": 0.505 (percent), ...}}
    public static BasicFinancials ParseBasicFinancials(JsonElement financials)
    {
        var metric = financials.ValueKind == JsonValueKind.Object && financials.TryGetProperty("metric", out var found) && found.ValueKind == JsonValueKind.Object
            ? found
            : default;

        return new BasicFinancials(
            PeRatio: Number(metric, "peTTM"),
            EpsTtmUsd: Number(metric, "epsTTM"),
            DividendYieldPct: Number(metric, "dividendYieldIndicatedAnnual"),
            High52WeekUsd: Number(metric, "52WeekHigh"),
            Low52WeekUsd: Number(metric, "52WeekLow"),
            Beta: Number(metric, "beta"));
    }

    /// <returns>Newest month first; malformed months are skipped.</returns>
    public static IReadOnlyList<RecommendationTrend> ParseRecommendationTrends(JsonElement trends)
    {
        if (trends.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<RecommendationTrend>();
        foreach (var month in trends.EnumerateArray())
        {
            if (!DateOnly.TryParseExact(Text(month, "period"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var period)
                || Count(month, "strongBuy") is not { } strongBuy
                || Count(month, "buy") is not { } buy
                || Count(month, "hold") is not { } hold
                || Count(month, "sell") is not { } sell
                || Count(month, "strongSell") is not { } strongSell)
            {
                continue;
            }

            parsed.Add(new RecommendationTrend(period, strongBuy, buy, hold, sell, strongSell));
        }

        // One row per month: a repeated month would collide on the table's key and fail every refresh.
        return parsed
            .GroupBy(trend => trend.Period)
            .Select(month => month.First())
            .OrderByDescending(trend => trend.Period)
            .ToList();
    }

    // {"earningsCalendar": [{"date": "2027-01-27", "hour": "amc", ...}]}
    public static DateOnly? ParseNextEarningsDate(JsonElement calendar, DateOnly fromDate)
    {
        if (calendar.ValueKind != JsonValueKind.Object
            || !calendar.TryGetProperty("earningsCalendar", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return entries.EnumerateArray()
            .Select(entry => DateOnly.TryParseExact(Text(entry, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : (DateOnly?)null)
            .Where(date => date >= fromDate)
            .Min();
    }

    private async Task<JsonDocument> GetJsonAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(pathAndQuery, cancellationToken);
        ProviderRateLimitedException.ThrowIfLimited(response, SourceName);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
    }

    private static string? Text(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || value.GetString()?.Trim() is not { Length: > 0 } text)
        {
            return null;
        }

        return text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }

    // Read from the JSON number text, so values never pass through a binary float.
    private static decimal? Number(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out var number)
            ? number
            : null;

    private static int? Count(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var count)
        && count >= 0
            ? count
            : null;

    /// <summary>Only absolute https links are kept: they are rendered as links and logo sources.</summary>
    private static Uri? HttpsUrl(JsonElement parent, string property) =>
        Text(parent, property) is { } text
        && Uri.TryCreate(text, UriKind.Absolute, out var url)
        && url.Scheme == Uri.UriSchemeHttps
            ? url
            : null;

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
