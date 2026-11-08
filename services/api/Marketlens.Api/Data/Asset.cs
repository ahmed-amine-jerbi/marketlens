using Marketlens.Api.MarketData;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marketlens.Api.Data;

public sealed class Asset
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>The asset key as text (<c>crypto:BTC</c>); unique.</summary>
    public required string Key { get; init; }

    public required AssetClass Class { get; init; }

    public required string Symbol { get; init; }

    public required string Name { get; set; }

    public string? LogoUrl { get; set; }

    /// <summary>For ETFs, what the fund tracks ("S&amp;P 500"), so the UI never labels an ETF as the index itself.</summary>
    public string? Tracks { get; set; }

    public ProviderRefs ProviderRefs { get; set; } = new();

    public bool IsActive { get; set; } = true;
}

/// <summary>The asset's identifier at each provider that serves it; null where a provider doesn't.</summary>
public sealed record ProviderRefs
{
    public string? BinanceSymbol { get; init; }

    public string? CoinGeckoId { get; init; }

    public string? FinnhubSymbol { get; init; }

    public string? TwelveDataSymbol { get; init; }
}

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> builder)
    {
        builder.ToTable("assets");
        builder.HasKey(asset => asset.Id);
        builder.HasIndex(asset => asset.Key).IsUnique();
        builder.HasIndex(asset => new { asset.Class, asset.IsActive });

        builder.Property(asset => asset.Key).HasMaxLength(24);
        builder.Property(asset => asset.Class)
            .HasConversion(assetClass => AssetKey.ClassName(assetClass), name => ParseClass(name))
            .HasMaxLength(8);
        builder.Property(asset => asset.Symbol).HasMaxLength(16);
        builder.Property(asset => asset.Name).HasMaxLength(120);
        builder.Property(asset => asset.LogoUrl).HasMaxLength(500);
        builder.Property(asset => asset.Tracks).HasMaxLength(120);
        builder.ComplexProperty(asset => asset.ProviderRefs, refs => refs.ToJson().HasColumnType("jsonb"));
    }

    private static AssetClass ParseClass(string name) =>
        AssetKey.TryParseClass(name, out var assetClass)
            ? assetClass
            : throw new InvalidOperationException($"Unknown asset class '{name}' in the assets table.");
}
