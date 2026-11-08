using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketlens.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddForecasts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "forecasts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    series_end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    last_close_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    model = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    horizon_days = table.Column<int>(type: "integer", nullable: false),
                    backtest_baseline_error_pct = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    backtest_beats_baseline = table.Column<bool>(type: "boolean", nullable: false),
                    backtest_coverage_80_pct = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    backtest_mean_error_pct = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    backtest_window_weeks = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_forecasts", x => x.id);
                    table.ForeignKey(
                        name: "fk_forecasts_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "forecast_evaluations",
                columns: table => new
                {
                    forecast_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_date = table.Column<DateOnly>(type: "date", nullable: false),
                    actual_close_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    is_inside_range_80 = table.Column<bool>(type: "boolean", nullable: false),
                    error_pct = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    baseline_error_pct = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_forecast_evaluations", x => new { x.forecast_id, x.target_date });
                    table.ForeignKey(
                        name: "fk_forecast_evaluations_forecasts_forecast_id",
                        column: x => x.forecast_id,
                        principalTable: "forecasts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "forecast_points",
                columns: table => new
                {
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    forecast_id = table.Column<Guid>(type: "uuid", nullable: false),
                    median_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    low_80_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    high_80_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    low_95_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    high_95_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_forecast_points", x => new { x.forecast_id, x.date });
                    table.ForeignKey(
                        name: "fk_forecast_points_forecasts_forecast_id",
                        column: x => x.forecast_id,
                        principalTable: "forecasts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_forecasts_asset_id_created_at_utc",
                table: "forecasts",
                columns: new[] { "asset_id", "created_at_utc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "forecast_evaluations");

            migrationBuilder.DropTable(
                name: "forecast_points");

            migrationBuilder.DropTable(
                name: "forecasts");
        }
    }
}
