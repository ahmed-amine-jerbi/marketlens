using Marketlens.Api.MarketData;
using Marketlens.Api.Tests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(MarketlensApiFactory))]

namespace Marketlens.Api.Tests;

/// <summary>
/// One API host for the test assembly, backed by a throwaway PostgreSQL container, so integration
/// tests never touch a developer's local database, and with providers answered from recorded
/// fixtures, so tests never call a live provider. Requires Docker.
/// </summary>
public sealed class MarketlensApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.11-alpine").Build();

    public async ValueTask InitializeAsync()
    {
        await postgres.StartAsync();

        // Start the host here, once: test classes run in parallel, and building the server lazily
        // from several of them at the same time would run startup (migrations, catalogue) twice.
        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("DATABASE_URL", postgres.GetConnectionString());
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<FrankfurterClient>().ConfigurePrimaryHttpMessageHandler(() => new ProviderFixtures());
            services.AddHttpClient<BinanceMarketDataClient>().ConfigurePrimaryHttpMessageHandler(() => new ProviderFixtures());

            // WebSocket streams can't be answered from fixtures; their frame parsing is unit-tested.
            services.Remove(services.Single(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(BinanceLiveFeed)));
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await postgres.DisposeAsync();
    }
}
