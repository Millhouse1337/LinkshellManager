using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotAllianceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllianceKey",
                table: "AttendanceSnapshots",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AllianceLeaderName",
                table: "AttendanceSnapshots",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllianceKey",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "AllianceLeaderName",
                table: "AttendanceSnapshots");
        }
    }
}
