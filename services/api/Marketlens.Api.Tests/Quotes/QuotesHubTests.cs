using System.Threading.Channels;

using Marketlens.Api.Http;
using Marketlens.Api.MarketData;
using Marketlens.Api.Quotes;

using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Marketlens.Api.Tests.Quotes;

public sealed class QuotesHubTests(MarketlensApiFactory factory)
{
    // Solana has no feed in the test host, so only the ticks these tests apply move it.
    private static readonly AssetKey Solana = AssetKey.Parse("crypto:SOL");
    private static readonly string[] EuroOnly = ["fiat:EUR"];
    private static readonly string[] SolanaOnly = ["crypto:SOL"];
    private static readonly string[] UnknownCoin = ["crypto:NOTACOIN"];
    private static readonly string[] BitcoinCashOnly = ["crypto:BCH"];
    private static readonly string[] BitcoinCashAndUnknown = ["crypto:BCH", "crypto:NOTACOIN"];

    [Fact]
    public async Task Subscribe_answers_with_current_quotes_in_the_display_currency()
    {
        await WaitForPricesAsync("fiat:EUR", "fiat:TND");
        await using var connection = await ConnectAsync();

        var quotes = await connection.InvokeAsync<List<QuoteResponse>>("Subscribe", EuroOnly, "fiat:TND", TestContext.Current.CancellationToken);

        var euro = Assert.Single(quotes);
        Assert.Equal(AssetKey.Parse("fiat:EUR"), euro.Key);
        Assert.Equal(AssetKey.Parse("fiat:TND"), euro.In);
        Assert.InRange(euro.Price, 3.3m, 3.5m);
    }

    [Fact]
    public async Task A_new_price_is_pushed_to_subscribers_already_converted()
    {
        await WaitForPricesAsync("fiat:EUR");
        await using var connection = await ConnectAsync();
        var updates = Channel.CreateUnbounded<QuoteResponse>();
        connection.On<QuoteResponse>(QuotesHub.QuoteUpdatedMethod, quote => updates.Writer.TryWrite(quote));
        await connection.InvokeAsync<List<QuoteResponse>>("Subscribe", SolanaOnly, "fiat:EUR", TestContext.Current.CancellationToken);

        factory.Services.GetRequiredService<QuoteBook>().Apply(new PriceTick(Solana, 151.25m, 148.00m, DateTime.UtcNow, "binance", Freshness.Live));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var pushed = await updates.Reader.ReadAsync(timeout.Token);
        Assert.Equal(Solana, pushed.Key);
        Assert.Equal(AssetKey.Parse("fiat:EUR"), pushed.In);
        Assert.Equal(ChangeBasis.Rolling24h, pushed.ChangeBasis);
        Assert.True(pushed.Price < 151.25m);
    }

    [Fact]
    public async Task Subscribe_refuses_more_than_fifty_assets_per_connection()
    {
        await using var connection = await ConnectAsync();
        var catalog = factory.Services.GetRequiredService<AssetCatalog>();
        var fiftyOne = catalog.ActiveOf(AssetClass.Fiat).Take(QuotesHub.MaxKeysPerConnection + 1).Select(entry => entry.Key.ToString()).ToArray();

        var error = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync<List<QuoteResponse>>("Subscribe", fiftyOne, null, TestContext.Current.CancellationToken));

        Assert.Contains("at most 50", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Subscribe_refuses_an_unknown_asset()
    {
        await using var connection = await ConnectAsync();

        var error = await Assert.ThrowsAsync<HubException>(() =>
            connection.InvokeAsync<List<QuoteResponse>>("Subscribe", UnknownCoin, null, TestContext.Current.CancellationToken));

        Assert.Contains("crypto:NOTACOIN", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_subscribe_changes_nothing_so_it_cannot_unwatch_another_clients_asset()
    {
        var subscriptions = factory.Services.GetRequiredService<QuoteSubscriptions>();
        var bitcoinCashInUsd = new QuoteSubscription(AssetKey.Parse("crypto:BCH"), AssetKey.UsDollar);
        await using var watcher = await ConnectAsync();
        await using var other = await ConnectAsync();
        await watcher.InvokeAsync<List<QuoteResponse>>("Subscribe", BitcoinCashOnly, null, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<HubException>(() =>
            other.InvokeAsync<List<QuoteResponse>>("Subscribe", BitcoinCashAndUnknown, null, TestContext.Current.CancellationToken));
        await other.InvokeAsync("Unsubscribe", BitcoinCashOnly, TestContext.Current.CancellationToken);

        Assert.Contains(bitcoinCashInUsd, subscriptions.Active);
    }

    [Fact]
    public async Task A_sixth_connection_from_the_same_client_is_closed()
    {
        var connections = new List<HubConnection>();
        try
        {
            for (var opened = 0; opened < QuotesHub.MaxConnectionsPerClient; opened++)
            {
                connections.Add(await ConnectAsync());
            }

            var sixth = await ConnectAsync();
            connections.Add(sixth);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                sixth.InvokeAsync<List<QuoteResponse>>("Subscribe", EuroOnly, null, TestContext.Current.CancellationToken));
        }
        finally
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private async Task<HubConnection> ConnectAsync()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, QuotesHub.Path.TrimStart('/')), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .AddJsonProtocol(json => ApiConventions.ConfigureJson(json.PayloadSerializerOptions))
            .Build();
        await connection.StartAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private async Task WaitForPricesAsync(params string[] keys)
    {
        var quoteBook = factory.Services.GetRequiredService<QuoteBook>();
        Assert.True(await Eventually.IsTrueAsync(() => keys.All(key => quoteBook.TryGet(AssetKey.Parse(key), out _))));
    }
}
