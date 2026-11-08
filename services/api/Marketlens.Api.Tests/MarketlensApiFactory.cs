using Marketlens.Api.Tests;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(MarketlensApiFactory))]

namespace Marketlens.Api.Tests;

/// <summary>
/// One API host for the test assembly, backed by a throwaway PostgreSQL container, so integration
/// tests never touch a developer's local database. Requires Docker.
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
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await postgres.DisposeAsync();
    }
}
