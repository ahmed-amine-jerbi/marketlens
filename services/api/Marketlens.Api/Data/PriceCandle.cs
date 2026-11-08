using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marketlens.Api.Data;

public enum CandleInterval
{
    OneMinute,
    OneHour,
    OneDay,
}

/// <summary>Open, high, low and close in USD for one interval bucket of one asset.</summary>
public sealed class PriceCandle
{
    public required Guid AssetId { get; init; }

    public required CandleInterval Interval { get; init; }

    public required DateTime BucketStartUtc { get; init; }

    public required decimal OpenUsd { get; set; }

    public required decimal HighUsd { get; set; }

    public required decimal LowUsd { get; set; }

    public required decimal CloseUsd { get; set; }

    public decimal? Volume { get; set; }

    public required string Source { get; set; }
}

internal sealed class PriceCandleConfiguration : IEntityTypeConfiguration<PriceCandle>
{
    public void Configure(EntityTypeBuilder<PriceCandle> builder)
    {
        builder.ToTable("price_candles");
        // History reads are range scans on this key; it is also the idempotent upsert key.
        builder.HasKey(candle => new { candle.AssetId, candle.Interval, candle.BucketStartUtc });
        builder.HasOne<Asset>().WithMany().HasForeignKey(candle => candle.AssetId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(candle => candle.Interval)
            .HasConversion(interval => ToColumnValue(interval), value => FromColumnValue(value))
            .HasMaxLength(2);
        builder.Property(candle => candle.OpenUsd).HasPrecision(38, 18);
        builder.Property(candle => candle.HighUsd).HasPrecision(38, 18);
        builder.Property(candle => candle.LowUsd).HasPrecision(38, 18);
        builder.Property(candle => candle.CloseUsd).HasPrecision(38, 18);
        builder.Property(candle => candle.Volume).HasPrecision(38, 8);
        builder.Property(candle => candle.Source).HasMaxLength(32);
    }

    internal static string ToColumnValue(CandleInterval interval) => interval switch
    {
        CandleInterval.OneMinute => "1m",
        CandleInterval.OneHour => "1h",
        CandleInterval.OneDay => "1d",
        _ => throw new ArgumentOutOfRangeException(nameof(interval), interval, null),
    };

    private static CandleInterval FromColumnValue(string value) => value switch
    {
        "1m" => CandleInterval.OneMinute,
        "1h" => CandleInterval.OneHour,
        "1d" => CandleInterval.OneDay,
        _ => throw new InvalidOperationException($"Unknown candle interval '{value}' in price_candles."),
    };
}
