using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAuctionLootBlock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LootBlockCooldownHours",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "LootBiddingBlockedUntil",
                table: "AppUserLinkshells");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LootBlockCooldownHours",
                table: "Linkshells",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LootBiddingBlockedUntil",
                table: "AppUserLinkshells",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
