using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAuctionAvailableDkpAndLootBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue 24 so existing linkshells inherit the same 24h
            // default as new ones (the C# model initializer). 0 would have
            // silently disabled the block for every pre-existing linkshell.
            migrationBuilder.AddColumn<int>(
                name: "LootBlockCooldownHours",
                table: "Linkshells",
                type: "integer",
                nullable: false,
                defaultValue: 24);

            migrationBuilder.AddColumn<DateTime>(
                name: "LootBiddingBlockedUntil",
                table: "AppUserLinkshells",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LootBlockCooldownHours",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "LootBiddingBlockedUntil",
                table: "AppUserLinkshells");
        }
    }
}
