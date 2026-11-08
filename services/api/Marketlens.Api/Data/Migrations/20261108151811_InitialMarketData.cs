using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Marketlens.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialMarketData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    @class = table.Column<string>(name: "class", type: "character varying(8)", maxLength: 8, nullable: false),
                    symbol = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    logo_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    tracks = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    provider_refs_binance_symbol = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    provider_refs_coin_gecko_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    provider_refs_finnhub_symbol = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    provider_refs_twelve_data_symbol = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "price_candles",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    interval = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    bucket_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    open_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    high_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    low_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    close_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    volume = table.Column<decimal>(type: "numeric(38,8)", precision: 38, scale: 8, nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_candles", x => new { x.asset_id, x.interval, x.bucket_start_utc });
                    table.ForeignKey(
                        name: "fk_price_candles_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quote_snapshots",
                columns: table => new
                {
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: false),
                    reference_price_usd = table.Column<decimal>(type: "numeric(38,18)", precision: 38, scale: 18, nullable: true),
                    as_of_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_quote_snapshots", x => x.asset_id);
                    table.ForeignKey(
                        name: "fk_quote_snapshots_assets_asset_id",
                        column: x => x.asset_id,
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assets_class_is_active",
                table: "assets",
                columns: new[] { "class", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_assets_key",
                table: "assets",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_protection_keys");

            migrationBuilder.DropTable(
                name: "price_candles");

            migrationBuilder.DropTable(
                name: "quote_snapshots");

            migrationBuilder.DropTable(
                name: "assets");
        }
    }
}
