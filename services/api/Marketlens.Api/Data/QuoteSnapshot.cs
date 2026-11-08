using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marketlens.Api.Data;

/// <summary>
/// The last known USD price of an asset, written periodically so the in-memory quote book starts warm
/// after a restart instead of empty.
/// </summary>
public sealed class QuoteSnapshot
{
    public required Guid AssetId { get; init; }

    public required decimal PriceUsd { get; set; }

    /// <summary>The reference price <see cref="PriceUsd"/> is compared with: previous close, or 24 hours ago for crypto.</summary>
    public decimal? ReferencePriceUsd { get; set; }

    public required DateTime AsOfUtc { get; set; }

    public required string Source { get; set; }
}

internal sealed class QuoteSnapshotConfiguration : IEntityTypeConfiguration<QuoteSnapshot>
{
    public void Configure(EntityTypeBuilder<QuoteSnapshot> builder)
    {
        builder.ToTable("quote_snapshots");
        builder.HasKey(snapshot => snapshot.AssetId);
        builder.HasOne<Asset>().WithOne().HasForeignKey<QuoteSnapshot>(snapshot => snapshot.AssetId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(snapshot => snapshot.PriceUsd).HasPrecision(38, 18);
        builder.Property(snapshot => snapshot.ReferencePriceUsd).HasPrecision(38, 18);
        builder.Property(snapshot => snapshot.Source).HasMaxLength(32);
    }
}
