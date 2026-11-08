using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace Marketlens.Api.MarketData;

/// <summary>
/// Live crypto prices: one WebSocket to Binance's combined mini-ticker stream for every listed coin,
/// shared by all viewers. Each frame carries the last price and the price 24 hours earlier, which is
/// the rolling-24h change basis for crypto (D40). Reconnects with backoff; Binance also closes every
/// connection after 24 hours by design.
/// </summary>
public sealed partial class BinanceLiveFeed(
    AssetCatalog catalog,
    QuoteBook quoteBook,
    IOptions<BinanceOptions> options,
    TimeProvider timeProvider,
    ILogger<BinanceLiveFeed> logger) : BackgroundService
{
    private const int ReceiveBufferBytes = 8 * 1024;

    private readonly ProviderBackoff backoff = new();

    /// <summary>
    /// Reads one combined-stream frame (<c>{"stream": "...", "data": {"e": "24hrMiniTicker", ...}}</c>).
    /// Returns null for a frame that isn't a mini-ticker for a listed coin.
    /// </summary>
    public static PriceTick? ParseFrame(ReadOnlySpan<byte> utf8Frame, IReadOnlyDictionary<string, AssetKey> assetByBinanceSymbol)
    {
        ArgumentNullException.ThrowIfNull(assetByBinanceSymbol);

        JsonDocument frame;
        try
        {
            var reader = new Utf8JsonReader(utf8Frame);
            frame = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            // A malformed frame is skipped; the next one arrives within a second.
            return null;
        }

        using (frame)
        {
            if (!frame.RootElement.TryGetProperty("data", out var ticker)
                || ticker.ValueKind != JsonValueKind.Object
                || !ticker.TryGetProperty("s", out var symbol)
                || symbol.ValueKind != JsonValueKind.String
                || symbol.GetString() is not { } binanceSymbol
                || !assetByBinanceSymbol.TryGetValue(binanceSymbol, out var asset)
                || !TryReadDecimal(ticker, "c", out var lastPrice)
                || !TryReadDecimal(ticker, "o", out var priceDayAgo)
                || !ticker.TryGetProperty("E", out var eventTime)
                || eventTime.ValueKind != JsonValueKind.Number
                || !eventTime.TryGetInt64(out var eventTimeMs)
                || lastPrice <= 0)
            {
                return null;
            }

            return new PriceTick(
                asset,
                lastPrice,
                priceDayAgo > 0 ? priceDayAgo : null,
                DateTime.UnixEpoch.AddMilliseconds(eventTimeMs),
                BinanceMarketDataClient.SourceName,
                Freshness.Live);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StreamAsync(stoppingToken);
            }
            // Any provider failure, including an unexpected response shape, is logged and retried:
            // an exception escaping a background service would stop the whole API.
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                LogStreamFailed(logger, error);
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await Task.Delay(backoff.NextDelay(), timeProvider, stoppingToken);
        }
    }

    private async Task StreamAsync(CancellationToken cancellationToken)
    {
        var assetByBinanceSymbol = catalog.ActiveOf(AssetClass.Crypto)
            .Where(coin => coin.ProviderRefs.BinanceSymbol is not null)
            .ToDictionary(coin => coin.ProviderRefs.BinanceSymbol!, coin => coin.Key);
        if (assetByBinanceSymbol.Count == 0)
        {
            return;
        }

        var streams = string.Join('/', assetByBinanceSymbol.Keys.Select(symbol => symbol.ToLowerInvariant() + "@miniTicker"));
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await socket.ConnectAsync(new Uri(options.Value.StreamBaseUrl, $"stream?streams={streams}"), cancellationToken);
        LogConnected(logger, assetByBinanceSymbol.Count);

        var frame = new ArrayBufferWriter<byte>(ReceiveBufferBytes);
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            frame.ResetWrittenCount();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(frame.GetMemory(ReceiveBufferBytes), cancellationToken);
                frame.Advance(received.Count);
            }
            while (!received.EndOfMessage && received.MessageType != WebSocketMessageType.Close);

            if (received.MessageType == WebSocketMessageType.Close)
            {
                LogClosedByProvider(logger, socket.CloseStatus, socket.CloseStatusDescription);
                return;
            }

            if (ParseFrame(frame.WrittenSpan, assetByBinanceSymbol) is { } tick)
            {
                quoteBook.Apply(tick);
                backoff.Reset();
            }
        }
    }

    private static bool TryReadDecimal(JsonElement ticker, string property, out decimal value)
    {
        value = 0;
        return ticker.TryGetProperty(property, out var field)
            && field.ValueKind == JsonValueKind.String
            && field.GetString() is { } text
            && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Binance stream connected for {CoinCount} coins")]
    private static partial void LogConnected(ILogger logger, int coinCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Binance closed the stream ({CloseStatus}: {Reason}); reconnecting")]
    private static partial void LogClosedByProvider(ILogger logger, WebSocketCloseStatus? closeStatus, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Binance stream failed; reconnecting with backoff")]
    private static partial void LogStreamFailed(ILogger logger, Exception error);
}
