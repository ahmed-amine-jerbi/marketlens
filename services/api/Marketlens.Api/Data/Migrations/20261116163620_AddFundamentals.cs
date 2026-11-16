using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketlens.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFundamentals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analyst_recommendations",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_date = table.Column<DateOnly>(type: "date", nullable: false),
                    strong_buy = table.Column<int>(type: "integer", nullable: false),
                    buy = table.Column<int>(type: "integer", nullable: false),
                    hold = table.Column<int>(type: "integer", nullable: false),
                    sell = table.Column<int>(type: "integer", nullable: false),
                    strong_sell = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_analyst_recommendations", x => new { x.asset_id, x.period_date });
                    table.ForeignKey(
                        name: "fk_analyst_recommendations_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asset_financial_years",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    period_start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    revenue_usd = table.Column<decimal>(type: "numeric(24,4)", precision: 24, scale: 4, nullable: false),
                    net_income_usd = table.Column<decimal>(type: "numeric(24,4)", precision: 24, scale: 4, nullable: true),
                    filed_date = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_financial_years", x => new { x.asset_id, x.period_end_date });
                    table.ForeignKey(
                        name: "fk_asset_financial_years_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asset_fundamentals",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    industry = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    country = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    exchange = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ipo_date = table.Column<DateOnly>(type: "date", nullable: true),
                    website_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    market_cap_usd = table.Column<decimal>(type: "numeric(24,4)", precision: 24, scale: 4, nullable: true),
                    market_cap_price_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    shares_outstanding = table.Column<decimal>(type: "numeric(24,4)", precision: 24, scale: 4, nullable: true),
                    pe_ratio = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    eps_ttm_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    dividend_yield_pct = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    high_52_week_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    low_52_week_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    beta = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    next_earnings_date = table.Column<DateOnly>(type: "date", nullable: true),
                    finnhub_fetched_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sec_fetched_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asset_fundamentals", x => x.asset_id);
                    table.ForeignKey(
                        name: "fk_asset_fundamentals_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analyst_recommendations");

            migrationBuilder.DropTable(
                name: "asset_financial_years");

            migrationBuilder.DropTable(
                name: "asset_fundamentals");
        }
    }
}
