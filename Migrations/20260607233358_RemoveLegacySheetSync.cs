using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacySheetSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttInputDefaultEntryType",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "AttInputTabName",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "GoogleSheetTabName",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "ManualPointsTabName",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "SheetSyncEnabled",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "AttInputEntryType",
                table: "Events");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttInputDefaultEntryType",
                table: "Linkshells",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttInputTabName",
                table: "Linkshells",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GoogleSheetTabName",
                table: "Linkshells",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualPointsTabName",
                table: "Linkshells",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SheetSyncEnabled",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AttInputEntryType",
                table: "Events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }
    }
}
