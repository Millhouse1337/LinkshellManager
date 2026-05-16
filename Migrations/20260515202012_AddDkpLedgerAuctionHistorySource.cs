using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddDkpLedgerAuctionHistorySource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FK from DkpLedgerEntries -> AuctionHistories so a single auction
            // close produces one batched ManualPoints column on the sheet
            // instead of N separate columns.
            migrationBuilder.AddColumn<int>(
                name: "SourceAuctionHistoryId",
                table: "DkpLedgerEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_SourceAuctionHistoryId",
                table: "DkpLedgerEntries",
                column: "SourceAuctionHistoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_DkpLedgerEntries_AuctionHistories_SourceAuctionHistoryId",
                table: "DkpLedgerEntries",
                column: "SourceAuctionHistoryId",
                principalTable: "AuctionHistories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DkpLedgerEntries_AuctionHistories_SourceAuctionHistoryId",
                table: "DkpLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_DkpLedgerEntries_SourceAuctionHistoryId",
                table: "DkpLedgerEntries");

            migrationBuilder.DropColumn(
                name: "SourceAuctionHistoryId",
                table: "DkpLedgerEntries");
        }
    }
}
