using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowAttendanceAlliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllianceKey",
                table: "AppUserEventWindows",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AllianceNumber",
                table: "AppUserEventWindows",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllianceKey",
                table: "AppUserEventWindows");

            migrationBuilder.DropColumn(
                name: "AllianceNumber",
                table: "AppUserEventWindows");
        }
    }
}
