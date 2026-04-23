using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkshellCustomization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableAuctions",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableEndgame",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableHnmSection",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableMissions",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableToDs",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "HybridDkpPercentage",
                table: "Linkshells",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LootStructure",
                table: "Linkshells",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Dkp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableAuctions",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "EnableEndgame",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "EnableHnmSection",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "EnableMissions",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "EnableToDs",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "HybridDkpPercentage",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "LootStructure",
                table: "Linkshells");
        }
    }
}
