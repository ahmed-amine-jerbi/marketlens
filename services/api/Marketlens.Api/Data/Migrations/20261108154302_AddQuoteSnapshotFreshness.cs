using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Marketlens.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQuoteSnapshotFreshness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "freshness",
                table: "quote_snapshots",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                // Snapshots saved before this column existed are of unknown freshness; "delayed" is the honest label.
                defaultValue: "delayed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "freshness",
                table: "quote_snapshots");
        }
    }
}
