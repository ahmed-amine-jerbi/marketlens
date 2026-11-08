using System.Globalization;
using System.Text.Json;

using Marketlens.Api.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marketlens.Api.MarketData;

public sealed class TwelveDataOptions
{
    public const string Section = "MarketData:TwelveData";

    /// <summary>From <c>TWELVEDATA_API_KEY</c>. Without it, stock history comes only from live prices (or the sample feed).</summary>
    public string? ApiKey { get; set; }

    public Uri BaseUrl { get; set; } = new("https://api.twelvedata.com/");

    /// <summary>The free plan allows 8 requests a minute.</summary>
    public TimeSpan RequestSpacing { get; set; } = TimeSpan.FromSeconds(8);

    public TimeSpan TopUpInterval { get; set; } = TimeSpan.FromDays(1);

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Adapter for Twelve Data's daily time series (stock and ETF history, D28). The key travels in the
/// Authorization header, never in a URL.
/// </summary>
public sealed class TwelveDataClient(HttpClient http)
{
    public const string SourceName = "twelvedata";

    /// <summary>The most bars one time-series request returns.</summary>
    public const int MaxBarsPerRequest = 5000;

    public async Task<IReadOnlyList<CandleRow>> FetchDailyCandlesAsync(AssetKey asset, string symbol, int barCount, CancellationToken cancellationToken)
    {
        var outputSize = Math.Clamp(barCount, 1, MaxBarsPerRequest);
        using var response = await http.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"time_series?symbol={Uri.EscapeDataString(symbol)}&interval=1day&outputsize={outputSize}&timezone=UTC"),
            cancellationToken);
        ProviderRateLimitedException.ThrowIfLimited(response, SourceName);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var series = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        return ParseDailySeries(asset, series.RootElement);
    }

    /// <summary>
    /// Reads <c>{"values": [{"datetime", "open", "high", "low", "close", "volume"}], "status": "ok"}</c>.
    /// Twelve Data reports errors, including rate limits, as <c>"status": "error"</c> with HTTP 200.
    /// </summary>
    public static IReadOnlyList<CandleRow> ParseDailySeries(AssetKey asset, JsonElement series)
    {
        if (series.TryGetProperty("status", out var status) && status.GetString() == "error")
        {
            var code = series.TryGetProperty("code", out var codeField) && codeField.TryGetInt32(out var value) ? value : 0;
            var message = series.TryGetProperty("message", out var messageField) ? messageField.GetString() : null;
            if (code == 429)
            {
                throw new ProviderRateLimitedException(SourceName, retryAfter: TimeSpan.FromMinutes(1));
            }

            throw new HttpRequestException($"Twelve Data refused {asset}: {code} {message}");
        }

        if (!series.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Twelve Data returned no values for {asset}.");
        }

        return values.EnumerateArray().Select(bar => new CandleRow(
            asset,
            CandleInterval.OneDay,
            DateTime.SpecifyKind(DateTime.ParseExact(bar.GetProperty("datetime").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc),
            ParseDecimal(bar, "open"),
            ParseDecimal(bar, "high"),
            ParseDecimal(bar, "low"),
            ParseDecimal(bar, "close"),
            bar.TryGetProperty("volume", out var volume) && decimal.TryParse(volume.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var shares) ? shares : null,
            SourceName)).ToList();
    }

    private static decimal ParseDecimal(JsonElement bar, string property) =>
        decimal.Parse(bar.GetProperty(property).GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture);
}

/// <summary>
/// Fills daily stock and ETF history from Twelve Data: five years on a fresh database, then only the
/// days since the newest stored candle, once a day, one request every 8 seconds. Only runs with a key.
/// </summary>
public sealed partial class TwelveDataHistoryBackfill(
    TwelveDataClient twelveData,
    AssetCatalog catalog,
    CandleWriter candleWriter,
    IServiceScopeFactory scopeFactory,
    IOptions<TwelveDataOptions> options,
    TimeProvider timeProvider,
    ILogger<TwelveDataHistoryBackfill> logger) : BackgroundService
{
    private readonly ProviderBackoff backoff = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.HasApiKey)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var listing in catalog.ActiveOf(AssetClass.Stock).Concat(catalog.ActiveOf(AssetClass.Etf)).ToList())
            {
                var delay = options.Value.RequestSpacing;
                try
                {
                    await TopUpAsync(listing, stoppingToken);
                    backoff.Reset();
                }
                // Any provider failure, including an unexpected response shape, is logged and retried:
                // an exception escaping a background service would stop the whole API.
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    delay = backoff.NextDelay((error as ProviderRateLimitedException)?.RetryAfter);
                    LogBackfillFailed(logger, error, listing.Key, delay);
                }

                await Task.Delay(delay, timeProvider, stoppingToken);
            }

            await Task.Delay(options.Value.TopUpInterval, timeProvider, stoppingToken);
        }
    }

    private async Task TopUpAsync(CatalogEntry listing, CancellationToken cancellationToken)
    {
        if (listing.ProviderRefs.TwelveDataSymbol is not { } symbol)
        {
            return;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var oldestWantedUtc = nowUtc - CandleWriter.RetentionWindow(CandleInterval.OneDay);
        var newestUtc = await NewestDailyBucketAsync(listing.Id, cancellationToken);
        var missingDays = (int)Math.Ceiling((nowUtc - (newestUtc ?? oldestWantedUtc)).TotalDays) + 1;
        if (newestUtc is not null && missingDays <= 1)
        {
            return;
        }

        var candles = await twelveData.FetchDailyCandlesAsync(listing.Key, symbol, missingDays, cancellationToken);
        var writtenCount = await candleWriter.UpsertAsync(candles.Where(candle => candle.BucketStartUtc >= oldestWantedUtc), cancellationToken);
        LogFilled(logger, listing.Key, writtenCount);
    }

    private async Task<DateTime?> NewestDailyBucketAsync(Guid assetId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>();
        return await db.PriceCandles
            .Where(candle => candle.AssetId == assetId && candle.Interval == CandleInterval.OneDay && candle.Source == TwelveDataClient.SourceName)
            .MaxAsync(candle => (DateTime?)candle.BucketStartUtc, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Twelve Data history for {Asset}: {CandleCount} daily candles")]
    private static partial void LogFilled(ILogger logger, AssetKey asset, int candleCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Twelve Data history for {Asset} failed; next request in {RetryDelay}")]
    private static partial void LogBackfillFailed(ILogger logger, Exception error, AssetKey asset, TimeSpan retryDelay);
}
