using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddItemSold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSold",
                table: "Items",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RevenueEntryId",
                table: "Items",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SoldAt",
                table: "Items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SoldByCharacterName",
                table: "Items",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SoldPrice",
                table: "Items",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSold",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "RevenueEntryId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "SoldAt",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "SoldByCharacterName",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "SoldPrice",
                table: "Items");
        }
    }
}
