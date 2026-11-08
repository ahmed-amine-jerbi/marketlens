using System.Buffers;
using System.Net.WebSockets;
using System.Text;

using Microsoft.Extensions.Options;

namespace Marketlens.Api.MarketData;

/// <summary>
/// Live US stock and ETF trades over Finnhub's single free WebSocket (at most 50 symbols, D27).
/// Only runs with a Finnhub key; trades arrive during US market hours only, and outside them the
/// quote poller keeps prices and previous closes current.
/// </summary>
public sealed partial class FinnhubLiveFeed(
    AssetCatalog catalog,
    AssetUniverse universe,
    QuoteBook quoteBook,
    IOptions<FinnhubOptions> options,
    TimeProvider timeProvider,
    ILogger<FinnhubLiveFeed> logger) : BackgroundService
{
    private const int ReceiveBufferBytes = 16 * 1024;

    private readonly ProviderBackoff backoff = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.HasApiKey)
        {
            return;
        }

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
        var streamedSymbols = universe.StreamedStocks.Select(stock => (Class: AssetClass.Stock, stock.Symbol))
            .Concat(universe.StreamedEtfs.Select(etf => (Class: AssetClass.Etf, etf.Symbol)))
            .Select(listing => new AssetKey(listing.Class, listing.Symbol))
            .Where(asset => catalog.TryGet(asset, out _))
            .ToDictionary(asset => asset.Symbol);

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        // Finnhub only accepts the key as a query parameter here; this URI is never logged.
        var streamUri = new Uri($"{options.Value.StreamUrl.ToString().TrimEnd('/')}?token={Uri.EscapeDataString(options.Value.ApiKey!)}");
        await socket.ConnectAsync(streamUri, cancellationToken);
        foreach (var symbol in streamedSymbols.Keys)
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(FinnhubClient.SubscribeMessage(symbol)), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        LogConnected(logger, streamedSymbols.Count);

        var message = new ArrayBufferWriter<byte>(ReceiveBufferBytes);
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            message.ResetWrittenCount();
            ValueWebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(message.GetMemory(ReceiveBufferBytes), cancellationToken);
                message.Advance(received.Count);
            }
            while (!received.EndOfMessage && received.MessageType != WebSocketMessageType.Close);

            if (received.MessageType == WebSocketMessageType.Close)
            {
                LogClosedByProvider(logger, socket.CloseStatus, socket.CloseStatusDescription);
                return;
            }

            foreach (var tick in FinnhubClient.ParseTradeMessage(message.WrittenSpan, streamedSymbols))
            {
                quoteBook.Apply(tick);
            }

            backoff.Reset();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Finnhub stream connected for {SymbolCount} symbols")]
    private static partial void LogConnected(ILogger logger, int symbolCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Finnhub closed the stream ({CloseStatus}: {Reason}); reconnecting")]
    private static partial void LogClosedByProvider(ILogger logger, WebSocketCloseStatus? closeStatus, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Finnhub stream failed; reconnecting with backoff")]
    private static partial void LogStreamFailed(ILogger logger, Exception error);
}

/// <summary>
/// Polls Finnhub quotes for every stock and ETF in turn, spread evenly over the poll cycle: it keeps
/// previous closes current for the change basis (D40) and prices the symbols the WebSocket doesn't
/// carry (marked delayed). Only runs with a Finnhub key.
/// </summary>
public sealed partial class FinnhubQuotePoller(
    FinnhubClient finnhub,
    AssetCatalog catalog,
    QuoteBook quoteBook,
    IOptions<FinnhubOptions> options,
    TimeProvider timeProvider,
    ILogger<FinnhubQuotePoller> logger) : BackgroundService
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
            var listings = catalog.ActiveOf(AssetClass.Stock).Concat(catalog.ActiveOf(AssetClass.Etf)).ToList();
            var spacing = listings.Count == 0 ? options.Value.QuotePollCycle : options.Value.QuotePollCycle / listings.Count;

            foreach (var listing in listings)
            {
                var delay = spacing;
                try
                {
                    await PollAsync(listing, stoppingToken);
                    backoff.Reset();
                }
                // Any provider failure, including an unexpected response shape, is logged and retried:
                // an exception escaping a background service would stop the whole API.
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    delay = backoff.NextDelay((error as ProviderRateLimitedException)?.RetryAfter);
                    LogPollFailed(logger, error, listing.Key, delay);
                }

                await Task.Delay(delay, timeProvider, stoppingToken);
            }
        }
    }

    private async Task PollAsync(CatalogEntry listing, CancellationToken cancellationToken)
    {
        if (listing.ProviderRefs.FinnhubSymbol is not { } symbol
            || await finnhub.FetchQuoteAsync(listing.Key, symbol, cancellationToken) is not { } quote)
        {
            return;
        }

        quoteBook.Apply(new PriceTick(quote.Asset, quote.PriceUsd, quote.PreviousCloseUsd, quote.TradedAtUtc, FinnhubClient.SourceName, Freshness.Delayed));
        if (quote.PreviousCloseUsd is { } previousClose)
        {
            quoteBook.SetReferencePrice(quote.Asset, previousClose);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Finnhub quote for {Asset} failed; next request in {RetryDelay}")]
    private static partial void LogPollFailed(ILogger logger, Exception error, AssetKey asset, TimeSpan retryDelay);
}
