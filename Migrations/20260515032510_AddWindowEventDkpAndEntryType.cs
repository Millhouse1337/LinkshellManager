using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowEventDkpAndEntryType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DkpAmount",
                table: "WindowEvents",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntryType",
                table: "WindowEvents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PostedToSheetAt",
                table: "WindowEvents",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DkpAmount",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "EntryType",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "PostedToSheetAt",
                table: "WindowEvents");
        }
    }
}
