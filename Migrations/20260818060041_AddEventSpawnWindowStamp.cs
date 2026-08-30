using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddEventSpawnWindowStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WindowCount",
                table: "WindowEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WindowMinutes",
                table: "WindowEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpawnWindowCount",
                table: "Events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SpawnWindowMinutes",
                table: "Events",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WindowCount",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "WindowMinutes",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "SpawnWindowCount",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "SpawnWindowMinutes",
                table: "Events");
        }
    }
}
