using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookAttendanceSnapshotToggle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PostAttendanceSnapshot",
                table: "LinkshellDiscordWebhooks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Snapshots previously fanned out to EVERY webhook. Preserve that
            // for already-configured channels so the opt-in change doesn't
            // silently stop posts; new rows default to false (opt-in).
            migrationBuilder.Sql(
                "UPDATE \"LinkshellDiscordWebhooks\" SET \"PostAttendanceSnapshot\" = TRUE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PostAttendanceSnapshot",
                table: "LinkshellDiscordWebhooks");
        }
    }
}
