using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotSlotKindAndMiscDkp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MiscDkpAmount",
                table: "WindowEvents",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SlotKind",
                table: "AttendanceSnapshots",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Window");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MiscDkpAmount",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "SlotKind",
                table: "AttendanceSnapshots");
        }
    }
}
