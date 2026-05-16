using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddDkpLedgerAttInputRowNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttInputRowNumber",
                table: "DkpLedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_AttInputRowNumber",
                table: "DkpLedgerEntries",
                column: "AttInputRowNumber");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DkpLedgerEntries_AttInputRowNumber",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "AttInputRowNumber",
                table: "DkpLedgerEntries");
        }
    }
}
