using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowEventLedgerSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceWindowEventId",
                table: "DkpLedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_SourceWindowEventId",
                table: "DkpLedgerEntries",
                column: "SourceWindowEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DkpLedgerEntries_SourceWindowEventId",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "SourceWindowEventId",
                table: "DkpLedgerEntries");
        }
    }
}
