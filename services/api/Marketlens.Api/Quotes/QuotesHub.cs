using System.Collections.Concurrent;

using Marketlens.Api.MarketData;

using Microsoft.AspNetCore.SignalR;

namespace Marketlens.Api.Quotes;

/// <summary>An asset watched in one display currency; one SignalR group each.</summary>
public readonly record struct QuoteSubscription(AssetKey Asset, AssetKey In)
{
    public string GroupName => $"quotes:{Asset}:{In}";
}

/// <summary>
/// Which subscriptions are watched, by how many connections, and the per-IP connection count. Shared
/// by the hub (which writes it) and <see cref="QuoteBroadcaster"/> (which reads it).
/// </summary>
public sealed class QuoteSubscriptions
{
    private readonly ConcurrentDictionary<QuoteSubscription, int> watcherCounts = new();
    private readonly ConcurrentDictionary<string, int> connectionsByClient = new();

    public IReadOnlyCollection<QuoteSubscription> Active => watcherCounts.Where(pair => pair.Value > 0).Select(pair => pair.Key).ToList();

    public void Watch(QuoteSubscription subscription) => watcherCounts.AddOrUpdate(subscription, 1, (_, count) => count + 1);

    public void Unwatch(QuoteSubscription subscription)
    {
        var remaining = watcherCounts.AddOrUpdate(subscription, 0, (_, count) => Math.Max(0, count - 1));
        if (remaining == 0)
        {
            watcherCounts.TryRemove(KeyValuePair.Create(subscription, 0));
        }
    }

    /// <returns>False when the client already holds the maximum number of connections.</returns>
    public bool TryOpenConnection(string client, int maxConnections)
    {
        var opened = connectionsByClient.AddOrUpdate(client, 1, (_, count) => count + 1);
        if (opened <= maxConnections)
        {
            return true;
        }

        CloseConnection(client);
        return false;
    }

    public void CloseConnection(string client)
    {
        var remaining = connectionsByClient.AddOrUpdate(client, 0, (_, count) => Math.Max(0, count - 1));
        if (remaining == 0)
        {
            connectionsByClient.TryRemove(KeyValuePair.Create(client, 0));
        }
    }
}

/// <summary>
/// Live quotes for anyone (R12): <c>Subscribe(keys, in)</c> answers with the current quotes and then
/// pushes <c>QuoteUpdated</c> as prices move, already converted into <c>in</c> (D36). Limits protect
/// the single API instance (R23): 50 keys per connection, 5 connections per client IP, and a
/// throttle on <c>Subscribe</c>.
/// </summary>
public sealed class QuotesHub(AssetCatalog catalog, QuoteReader quotes, QuoteSubscriptions subscriptions, TimeProvider timeProvider) : Hub
{
    public const string Path = "/hubs/quotes";
    public const string QuoteUpdatedMethod = "QuoteUpdated";
    public const int MaxKeysPerConnection = 50;
    public const int MaxConnectionsPerClient = 5;
    public const int MaxSubscribeCallsPerWindow = 10;

    private static readonly TimeSpan SubscribeWindow = TimeSpan.FromSeconds(10);

    private const string SubscriptionsItem = "subscriptions";
    private const string SubscribeCallsItem = "subscribe-calls";
    private const string ClientItem = "client";

    public override async Task OnConnectedAsync()
    {
        var client = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!subscriptions.TryOpenConnection(client, MaxConnectionsPerClient))
        {
            Context.Abort();
            return;
        }

        Context.Items[ClientItem] = client;
        Context.Items[SubscriptionsItem] = new HashSet<QuoteSubscription>();
        Context.Items[SubscribeCallsItem] = new Queue<DateTimeOffset>();
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(ClientItem, out var client) && client is string clientAddress)
        {
            subscriptions.CloseConnection(clientAddress);
        }

        foreach (var subscription in Subscriptions())
        {
            subscriptions.Unwatch(subscription);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <returns>The current quotes of the newly watched assets that have a price.</returns>
    public async Task<IReadOnlyList<QuoteResponse>> Subscribe(string[] keys, string? @in)
    {
        ArgumentNullException.ThrowIfNull(keys);
        EnforceSubscribeThrottle();

        if (!QuoteEndpoints.TryParseDisplayCurrency(catalog, @in, out var displayIn, out _))
        {
            throw new HubException("'in' must be a tracked asset key such as fiat:EUR.");
        }

        // Validate everything before changing anything: this connection's set and the shared watcher
        // counts must move together, or an unsubscribe could later decrement another client's count.
        var assets = ParseTrackedKeys(keys);
        var watched = Subscriptions();
        var requested = assets
            .Select(asset => new QuoteSubscription(asset, displayIn))
            .Where(subscription => !watched.Contains(subscription))
            .ToList();
        if (watched.Count + requested.Count > MaxKeysPerConnection)
        {
            throw new HubException($"A connection can watch at most {MaxKeysPerConnection} assets.");
        }

        foreach (var subscription in requested)
        {
            watched.Add(subscription);
            subscriptions.Watch(subscription);
            await Groups.AddToGroupAsync(Context.ConnectionId, subscription.GroupName);
        }

        return requested.Select(subscription => quotes.TryRead(subscription.Asset, subscription.In)).OfType<QuoteResponse>().ToList();
    }

    /// <summary>Stops watching the given assets in every display currency.</summary>
    public async Task Unsubscribe(string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var watched = Subscriptions();
        var assets = ParseTrackedKeys(keys).ToHashSet();
        foreach (var subscription in watched.Where(subscription => assets.Contains(subscription.Asset)).ToList())
        {
            watched.Remove(subscription);
            subscriptions.Unwatch(subscription);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, subscription.GroupName);
        }
    }

    /// <summary>Every key parsed and tracked, each asset once, or a <see cref="HubException"/> before anything changes.</summary>
    private List<AssetKey> ParseTrackedKeys(string[] keys)
    {
        var assets = new List<AssetKey>();
        foreach (var text in keys)
        {
            if (!AssetKey.TryParse(text, out var asset) || !catalog.TryGet(asset, out _))
            {
                throw new HubException($"'{text}' is not a tracked asset key.");
            }

            assets.Add(asset);
        }

        return assets.Distinct().ToList();
    }

    private void EnforceSubscribeThrottle()
    {
        var calls = (Queue<DateTimeOffset>)Context.Items[SubscribeCallsItem]!;
        var now = timeProvider.GetUtcNow();
        while (calls.Count > 0 && now - calls.Peek() > SubscribeWindow)
        {
            calls.Dequeue();
        }

        if (calls.Count >= MaxSubscribeCallsPerWindow)
        {
            throw new HubException("Too many subscribe calls; wait a few seconds.");
        }

        calls.Enqueue(now);
    }

    private HashSet<QuoteSubscription> Subscriptions() =>
        Context.Items.TryGetValue(SubscriptionsItem, out var items) && items is HashSet<QuoteSubscription> watched ? watched : [];
}
