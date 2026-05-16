using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddDkpAuditRelatedLedgerEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AuditRelatedLedgerEntryId",
                table: "DkpLedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_AuditRelatedLedgerEntryId",
                table: "DkpLedgerEntries",
                column: "AuditRelatedLedgerEntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DkpLedgerEntries_AuditRelatedLedgerEntryId",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "AuditRelatedLedgerEntryId",
                table: "DkpLedgerEntries");
        }
    }
}
