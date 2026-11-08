using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Marketlens.Api.Data;

/// <summary>
/// A forecast of one asset's USD price for the days after <see cref="SeriesEndDate"/>, from the
/// forecaster, with the backtest that decides whether it may be shown (D29, D54).
/// </summary>
public sealed class Forecast
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid AssetId { get; init; }

    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>The date of the last daily close the forecast was computed from.</summary>
    public required DateOnly SeriesEndDate { get; init; }

    /// <summary>The model the backtest chose, or <c>Naive</c> when no model beat "no change".</summary>
    public required string Model { get; init; }

    public required int HorizonDays { get; init; }

    public required ForecastBacktest Backtest { get; init; }

    public List<ForecastPoint> Points { get; init; } = [];
}

/// <summary>How the chosen model did over the backtest windows, next to the "no change" baseline.</summary>
public sealed record ForecastBacktest
{
    public required int WindowWeeks { get; init; }

    /// <summary>Share of actual closes that fell inside the 80% range, in percent.</summary>
    public required decimal Coverage80Pct { get; init; }

    public required decimal MeanErrorPct { get; init; }

    public required decimal BaselineErrorPct { get; init; }

    public required bool BeatsBaseline { get; init; }
}

/// <summary>One forecast day: the median and the 80% and 95% ranges, in USD.</summary>
public sealed class ForecastPoint
{
    public required DateOnly Date { get; init; }

    public required decimal MedianUsd { get; init; }

    public required decimal Low80Usd { get; init; }

    public required decimal High80Usd { get; init; }

    public required decimal Low95Usd { get; init; }

    public required decimal High95Usd { get; init; }
}

/// <summary>A forecast day compared with the close that actually happened: the live track record.</summary>
public sealed class ForecastEvaluation
{
    public required Guid ForecastId { get; init; }

    public required DateOnly TargetDate { get; init; }

    public required decimal ActualCloseUsd { get; init; }

    public required bool IsInsideRange80 { get; init; }

    /// <summary>Absolute error of the median, in percent of the actual close.</summary>
    public required decimal ErrorPct { get; init; }

    /// <summary>Absolute error of "no change" (the forecast's last input close), in percent of the actual close.</summary>
    public required decimal BaselineErrorPct { get; init; }
}

internal sealed class ForecastConfiguration : IEntityTypeConfiguration<Forecast>
{
    public void Configure(EntityTypeBuilder<Forecast> builder)
    {
        builder.ToTable("forecasts");
        builder.HasKey(forecast => forecast.Id);
        builder.HasOne<Asset>().WithMany().HasForeignKey(forecast => forecast.AssetId).OnDelete(DeleteBehavior.Cascade);
        // The endpoint and the scheduler read an asset's latest forecast.
        builder.HasIndex(forecast => new { forecast.AssetId, forecast.CreatedAtUtc }).IsDescending(false, true);

        builder.Property(forecast => forecast.Model).HasMaxLength(32);
        builder.ComplexProperty(forecast => forecast.Backtest, backtest =>
        {
            backtest.Property(summary => summary.Coverage80Pct).HasColumnName("backtest_coverage_80_pct").HasPrecision(12, 4);
            backtest.Property(summary => summary.MeanErrorPct).HasPrecision(12, 4);
            backtest.Property(summary => summary.BaselineErrorPct).HasPrecision(12, 4);
        });
        builder.OwnsMany(forecast => forecast.Points, points =>
        {
            points.ToTable("forecast_points");
            points.WithOwner().HasForeignKey("ForecastId");
            points.HasKey("ForecastId", nameof(ForecastPoint.Date));
            points.Property(point => point.MedianUsd).HasPrecision(38, 18);
            points.Property(point => point.Low80Usd).HasColumnName("low_80_usd").HasPrecision(38, 18);
            points.Property(point => point.High80Usd).HasColumnName("high_80_usd").HasPrecision(38, 18);
            points.Property(point => point.Low95Usd).HasColumnName("low_95_usd").HasPrecision(38, 18);
            points.Property(point => point.High95Usd).HasColumnName("high_95_usd").HasPrecision(38, 18);
        });
    }
}

internal sealed class ForecastEvaluationConfiguration : IEntityTypeConfiguration<ForecastEvaluation>
{
    public void Configure(EntityTypeBuilder<ForecastEvaluation> builder)
    {
        builder.ToTable("forecast_evaluations");
        builder.HasKey(evaluation => new { evaluation.ForecastId, evaluation.TargetDate });
        builder.HasOne<Forecast>().WithMany().HasForeignKey(evaluation => evaluation.ForecastId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(evaluation => evaluation.ActualCloseUsd).HasPrecision(38, 18);
        builder.Property(evaluation => evaluation.IsInsideRange80).HasColumnName("is_inside_range_80");
        builder.Property(evaluation => evaluation.ErrorPct).HasPrecision(12, 4);
        builder.Property(evaluation => evaluation.BaselineErrorPct).HasPrecision(12, 4);
    }
}
