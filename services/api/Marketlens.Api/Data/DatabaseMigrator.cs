using Microsoft.EntityFrameworkCore;

namespace Marketlens.Api.Data;

/// <summary>
/// Applies pending migrations before any other hosted service starts, so a fresh clone runs with one
/// command. Safe for v1's single API instance (ADR 0003); several instances would need a migration
/// step in the deployment instead.
/// </summary>
public sealed partial class DatabaseMigrator(IServiceScopeFactory scopeFactory, ILogger<DatabaseMigrator> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<MarketlensDbContext>().Database;

        var pending = (await database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        LogApplyingMigrations(logger, pending.Count, pending);
        await database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {MigrationCount} database migrations: {Migrations}")]
    private static partial void LogApplyingMigrations(ILogger logger, int migrationCount, IReadOnlyList<string> migrations);
}
