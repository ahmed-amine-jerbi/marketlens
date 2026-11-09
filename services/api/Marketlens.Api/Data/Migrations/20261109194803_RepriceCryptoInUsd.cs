using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketlens.Api.Data.Migrations
{
    /// <summary>
    /// Data only (ADR 0007): crypto candles and snapshots stored before this point are in USDT, which
    /// was counted as 1 USD. They are deleted so the workers refill them in USD at each day's USDT
    /// rate, and forecasts (with their points and evaluations) are deleted because they were made
    /// from those prices. Everything comes back from the providers within minutes.
    /// </summary>
    public partial class RepriceCryptoInUsd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM price_candles WHERE source = 'binance';");
            migrationBuilder.Sql("DELETE FROM quote_snapshots WHERE source = 'binance';");
            migrationBuilder.Sql("DELETE FROM forecasts;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: the deleted rows are refilled from the providers, not from here.
        }
    }
}
