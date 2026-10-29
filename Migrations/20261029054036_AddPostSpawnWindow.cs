using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPostSpawnWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SpawnWindow",
                table: "EventAttendanceWindows",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CampWindowId",
                table: "AttendanceSnapshots",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpawnWindow",
                table: "EventAttendanceWindows");

            migrationBuilder.DropColumn(
                name: "CampWindowId",
                table: "AttendanceSnapshots");
        }
    }
}
