using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AuctionItemSourceItemSetNull : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuctionItems_Items_SourceItemId",
                table: "AuctionItems");

            migrationBuilder.AddForeignKey(
                name: "FK_AuctionItems_Items_SourceItemId",
                table: "AuctionItems",
                column: "SourceItemId",
                principalTable: "Items",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuctionItems_Items_SourceItemId",
                table: "AuctionItems");

            migrationBuilder.AddForeignKey(
                name: "FK_AuctionItems_Items_SourceItemId",
                table: "AuctionItems",
                column: "SourceItemId",
                principalTable: "Items",
                principalColumn: "Id");
        }
    }
}
