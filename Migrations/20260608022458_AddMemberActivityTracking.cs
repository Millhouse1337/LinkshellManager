using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberActivityTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill defaults match the model initializers so existing rows get the
            // intended baseline: events/history "count", attendees are "credited", and
            // the streak thresholds are 3 absences -> inactive / 2 attendances -> active.
            migrationBuilder.AddColumn<int>(
                name: "ActiveAfterAttendances",
                table: "Linkshells",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<bool>(
                name: "EnableActivityTracking",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "InactiveAfterAbsences",
                table: "Linkshells",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<bool>(
                name: "CountsTowardActive",
                table: "Events",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "CountsTowardActive",
                table: "EventHistories",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ActiveCredit",
                table: "AppUserEventHistories",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveAfterAttendances",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "EnableActivityTracking",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "InactiveAfterAbsences",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "CountsTowardActive",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "CountsTowardActive",
                table: "EventHistories");

            migrationBuilder.DropColumn(
                name: "ActiveCredit",
                table: "AppUserEventHistories");
        }
    }
}
