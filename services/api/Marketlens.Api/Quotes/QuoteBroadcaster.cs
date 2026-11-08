using System.Collections.Concurrent;

using Marketlens.Api.MarketData;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Marketlens.Api.Quotes;

public sealed class QuoteBroadcastOptions
{
    public const string Section = "Quotes";

    /// <summary>At most one update per asset per interval reaches clients, however often it trades.</summary>
    public int BroadcastIntervalMs { get; set; } = 1000;
}

/// <summary>
/// Pushes moved prices to watching clients once per interval. A moved asset updates every group that
/// shows it; a moved display currency (a new euro rate) updates every group shown in it.
/// </summary>
public sealed partial class QuoteBroadcaster(
    QuoteBook quoteBook,
    QuoteReader quotes,
    QuoteSubscriptions subscriptions,
    IHubContext<QuotesHub> hub,
    IOptions<QuoteBroadcastOptions> options,
    TimeProvider timeProvider,
    ILogger<QuoteBroadcaster> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<AssetKey, byte> movedAssets = new();

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        quoteBook.QuoteUpdated += MarkMoved;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        quoteBook.QuoteUpdated -= MarkMoved;
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(options.Value.BroadcastIntervalMs);
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, timeProvider, stoppingToken);
            try
            {
                await BroadcastMovedAsync(stoppingToken);
            }
            // A failed send is logged and the next interval carries newer prices; an exception
            // escaping a background service would stop the whole API.
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                LogBroadcastFailed(logger, error);
            }
        }
    }

    private void MarkMoved(UsdQuote quote) => movedAssets[quote.Asset] = 0;

    private async Task BroadcastMovedAsync(CancellationToken cancellationToken)
    {
        var moved = new HashSet<AssetKey>();
        foreach (var asset in movedAssets.Keys)
        {
            if (movedAssets.TryRemove(asset, out _))
            {
                moved.Add(asset);
            }
        }

        if (moved.Count == 0)
        {
            return;
        }

        foreach (var subscription in subscriptions.Active.Where(subscription => moved.Contains(subscription.Asset) || moved.Contains(subscription.In)))
        {
            if (quotes.TryRead(subscription.Asset, subscription.In) is { } quote)
            {
                await hub.Clients.Group(subscription.GroupName).SendAsync(QuotesHub.QuoteUpdatedMethod, quote, cancellationToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Broadcasting live quotes failed; the next interval retries")]
    private static partial void LogBroadcastFailed(ILogger logger, Exception error);
}
