using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Marketlens.Api.Data;

public sealed class MarketlensDbContext(DbContextOptions<MarketlensDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<PriceCandle> PriceCandles => Set<PriceCandle>();

    public DbSet<QuoteSnapshot> QuoteSnapshots => Set<QuoteSnapshot>();

    /// <summary>The Data Protection key ring, so sign-ins and email links survive container rebuilds (D34).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MarketlensDbContext).Assembly);
        modelBuilder.Entity<DataProtectionKey>().ToTable("data_protection_keys");
    }
}
