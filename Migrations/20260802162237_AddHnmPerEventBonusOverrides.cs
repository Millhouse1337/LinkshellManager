using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddHnmPerEventBonusOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "HnmClaimBonusOverride",
                table: "Events",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HnmCloseBonusOverride",
                table: "Events",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HnmKillBonusOverride",
                table: "Events",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HnmOpenBonusOverride",
                table: "Events",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HnmClaimBonusOverride",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "HnmCloseBonusOverride",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "HnmKillBonusOverride",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "HnmOpenBonusOverride",
                table: "Events");
        }
    }
}
