using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddHnmStandardBonuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "HnmStandardClaimBonus",
                table: "Linkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "HnmStandardCloseBonus",
                table: "Linkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "HnmStandardKillBonus",
                table: "Linkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "HnmStandardOpenBonus",
                table: "Linkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HnmStandardClaimBonus",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "HnmStandardCloseBonus",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "HnmStandardKillBonus",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "HnmStandardOpenBonus",
                table: "Linkshells");
        }
    }
}
