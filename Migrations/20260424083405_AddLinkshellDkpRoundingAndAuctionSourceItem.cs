using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkshellDkpRoundingAndAuctionSourceItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DkpRoundingIncrement",
                table: "Linkshells",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Quarter");

            migrationBuilder.AddColumn<int>(
                name: "SourceItemId",
                table: "AuctionItems",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuctionItems_SourceItemId",
                table: "AuctionItems",
                column: "SourceItemId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuctionItems_Items_SourceItemId",
                table: "AuctionItems",
                column: "SourceItemId",
                principalTable: "Items",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuctionItems_Items_SourceItemId",
                table: "AuctionItems");

            migrationBuilder.DropIndex(
                name: "IX_AuctionItems_SourceItemId",
                table: "AuctionItems");

            migrationBuilder.DropColumn(
                name: "DkpRoundingIncrement",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "SourceItemId",
                table: "AuctionItems");
        }
    }
}
