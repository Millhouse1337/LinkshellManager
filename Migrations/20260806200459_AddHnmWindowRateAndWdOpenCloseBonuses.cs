using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddHnmWindowRateAndWdOpenCloseBonuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "HnmStandardWindowBonus",
                table: "Linkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "WdCloseBonus",
                table: "Linkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "WdOpenBonus",
                table: "Linkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HnmStandardWindowBonus",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "WdCloseBonus",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "WdOpenBonus",
                table: "Linkshells");
        }
    }
}
