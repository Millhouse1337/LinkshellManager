using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RenameNextWindowAtAddWindowAnchor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WdNextWindowAt carried "the next window opens at" — same meaning as the renamed,
            // no-longer-WD-only NextWindowAt, so preserve the value by renaming into it.
            migrationBuilder.RenameColumn(
                name: "WdNextWindowAt",
                table: "Events",
                newName: "NextWindowAt");

            // WindowAnchorAt is genuinely new (the window-1 anchor the advancer counts from).
            migrationBuilder.AddColumn<DateTime>(
                name: "WindowAnchorAt",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WindowAnchorAt",
                table: "Events");

            migrationBuilder.RenameColumn(
                name: "NextWindowAt",
                table: "Events",
                newName: "WdNextWindowAt");
        }
    }
}
