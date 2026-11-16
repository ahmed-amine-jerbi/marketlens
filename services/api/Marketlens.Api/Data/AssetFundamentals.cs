using Marketlens.Api.MarketData;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marketlens.Api.Data;

/// <summary>
/// A stock's key figures as last fetched: Finnhub's profile and basic financials, the next earnings
/// date, and when each source was last read. One row per stock; money in US dollars.
/// </summary>
public sealed class AssetFundamentals
{
    public required Guid AssetId { get; init; }

    public string? Industry { get; set; }

    public string? Country { get; set; }

    public string? Exchange { get; set; }

    public DateOnly? IpoDate { get; set; }

    public string? WebsiteUrl { get; set; }

    /// <summary>Finnhub's market cap when fetched; the API moves it with the live price (D24).</summary>
    public decimal? MarketCapUsd { get; set; }

    /// <summary>The USD price <see cref="MarketCapUsd"/> was read at.</summary>
    public decimal? MarketCapPriceUsd { get; set; }

    /// <summary>Null when Finnhub's count disagrees with its own market cap (D24).</summary>
    public decimal? SharesOutstanding { get; set; }

    public decimal? PeRatio { get; set; }

    public decimal? EpsTtmUsd { get; set; }

    public decimal? DividendYieldPct { get; set; }

    public decimal? High52WeekUsd { get; set; }

    public decimal? Low52WeekUsd { get; set; }

    public decimal? Beta { get; set; }

    public DateOnly? NextEarningsDate { get; set; }

    public DateTime? FinnhubFetchedAtUtc { get; set; }

    public DateTime? SecFetchedAtUtc { get; set; }
}

/// <summary>One fiscal year's revenue and net income from the company's 10-K (SEC EDGAR).</summary>
public sealed class AssetFinancialYear
{
    public required Guid AssetId { get; init; }

    public required DateOnly PeriodEndDate { get; init; }

    public required DateOnly PeriodStartDate { get; init; }

    public required decimal RevenueUsd { get; init; }

    public decimal? NetIncomeUsd { get; init; }

    public required DateOnly FiledDate { get; init; }
}

/// <summary>How many analysts rated a stock strong buy … strong sell in one month (Finnhub).</summary>
public sealed class AnalystRecommendation
{
    public required Guid AssetId { get; init; }

    public required DateOnly PeriodDate { get; init; }

    public required int StrongBuy { get; init; }

    public required int Buy { get; init; }

    public required int Hold { get; init; }

    public required int Sell { get; init; }

    public required int StrongSell { get; init; }
}

internal sealed class AssetFundamentalsConfiguration : IEntityTypeConfiguration<AssetFundamentals>
{
    /// <summary>Company totals reach trillions with cents to spare; at scale 18 they'd overflow .NET's decimal (D22).</summary>
    internal const int TotalPrecision = 24;
    internal const int TotalScale = 4;

    public void Configure(EntityTypeBuilder<AssetFundamentals> builder)
    {
        builder.ToTable("asset_fundamentals");
        builder.HasKey(fundamentals => fundamentals.AssetId);
        builder.HasOne<Asset>().WithOne().HasForeignKey<AssetFundamentals>(fundamentals => fundamentals.AssetId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(fundamentals => fundamentals.Industry).HasMaxLength(FinnhubClient.MaxTextLength);
        builder.Property(fundamentals => fundamentals.Country).HasMaxLength(FinnhubClient.MaxTextLength);
        builder.Property(fundamentals => fundamentals.Exchange).HasMaxLength(FinnhubClient.MaxTextLength);
        builder.Property(fundamentals => fundamentals.WebsiteUrl).HasMaxLength(2048);
        builder.Property(fundamentals => fundamentals.MarketCapUsd).HasPrecision(TotalPrecision, TotalScale);
        builder.Property(fundamentals => fundamentals.SharesOutstanding).HasPrecision(TotalPrecision, TotalScale);
        builder.Property(fundamentals => fundamentals.MarketCapPriceUsd).HasPrecision(38, 18);
        builder.Property(fundamentals => fundamentals.PeRatio).HasPrecision(38, 18);
        builder.Property(fundamentals => fundamentals.EpsTtmUsd).HasPrecision(38, 18);
        builder.Property(fundamentals => fundamentals.DividendYieldPct).HasPrecision(38, 18);
        builder.Property(fundamentals => fundamentals.High52WeekUsd).HasColumnName("high_52_week_usd").HasPrecision(38, 18);
        builder.Property(fundamentals => fundamentals.Low52WeekUsd).HasColumnName("low_52_week_usd").HasPrecision(38, 18);
        builder.Property(fundamentals => fundamentals.Beta).HasPrecision(38, 18);
    }
}

internal sealed class AssetFinancialYearConfiguration : IEntityTypeConfiguration<AssetFinancialYear>
{
    public void Configure(EntityTypeBuilder<AssetFinancialYear> builder)
    {
        builder.ToTable("asset_financial_years");
        builder.HasKey(year => new { year.AssetId, year.PeriodEndDate });
        builder.HasOne<Asset>().WithMany().HasForeignKey(year => year.AssetId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(year => year.RevenueUsd).HasPrecision(AssetFundamentalsConfiguration.TotalPrecision, AssetFundamentalsConfiguration.TotalScale);
        builder.Property(year => year.NetIncomeUsd).HasPrecision(AssetFundamentalsConfiguration.TotalPrecision, AssetFundamentalsConfiguration.TotalScale);
    }
}

internal sealed class AnalystRecommendationConfiguration : IEntityTypeConfiguration<AnalystRecommendation>
{
    public void Configure(EntityTypeBuilder<AnalystRecommendation> builder)
    {
        builder.ToTable("analyst_recommendations");
        builder.HasKey(recommendation => new { recommendation.AssetId, recommendation.PeriodDate });
        builder.HasOne<Asset>().WithMany().HasForeignKey(recommendation => recommendation.AssetId).OnDelete(DeleteBehavior.Cascade);
    }
}
