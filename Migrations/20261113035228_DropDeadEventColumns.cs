using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class DropDeadEventColumns : Migration
    {
        // Phase 4B, step 2. Drops event, history and attendance columns no code has read or written
        // since Phase 4A (write-only copies, Google Sheets leftovers, old board/verify fields) on
        // AppUserEventHistories, AppUserEvents, AppUserEventStatusLedgers, AttendanceSnapshots,
        // EventAttendanceWindows, EventHistories, Events, EventWindowRosterSnapshots and WindowEvents.
        // Down puts the columns back EMPTY; the values only come back from the pg_dump deploy.ps1
        // takes before migrating.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The old "Verify" status rows were the only ones that set Source and
            // EventAttendanceWindowId, and nothing has read them since Phase 4A. Production had 0
            // when counted on 2026-11-12; this is a safety net so no half-empty Verify row is left
            // behind once those two columns are gone.
            migrationBuilder.Sql("DELETE FROM \"AppUserEventStatusLedgers\" WHERE \"ActionType\" = 'Verify';");

            migrationBuilder.DropForeignKey(
                name: "FK_AppUserEventStatusLedgers_AspNetUsers_AppUserId",
                table: "AppUserEventStatusLedgers");

            migrationBuilder.DropForeignKey(
                name: "FK_AppUserEventStatusLedgers_EventAttendanceWindows_EventAtten~",
                table: "AppUserEventStatusLedgers");

            migrationBuilder.DropIndex(
                name: "IX_AppUserEventStatusLedgers_AppUserId",
                table: "AppUserEventStatusLedgers");

            migrationBuilder.DropIndex(
                name: "IX_AppUserEventStatusLedgers_EventAttendanceWindowId",
                table: "AppUserEventStatusLedgers");

            migrationBuilder.DropColumn(
                name: "AttInputRowCount",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "ClosedAtUtc",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "FirstAttInputRowNumber",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "AppUserId",
                table: "EventWindowRosterSnapshots");

            migrationBuilder.DropColumn(
                name: "AttInputAppendedAt",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "CreationSource",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "DiscordExtraMessageIds",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "EventDkp",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "WdAwaitingProcessingSince",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "Details",
                table: "EventHistories");

            migrationBuilder.DropColumn(
                name: "EventDkp",
                table: "EventHistories");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "EventHistories");

            migrationBuilder.DropColumn(
                name: "AttInputAppendedAt",
                table: "EventAttendanceWindows");

            migrationBuilder.DropColumn(
                name: "AttInputAppendedAt",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "UtcOffset",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "AppUserId",
                table: "AppUserEventStatusLedgers");

            migrationBuilder.DropColumn(
                name: "DeniedBy",
                table: "AppUserEventStatusLedgers");

            migrationBuilder.DropColumn(
                name: "EventAttendanceWindowId",
                table: "AppUserEventStatusLedgers");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "AppUserEventStatusLedgers");

            migrationBuilder.DropColumn(
                name: "VerifiedBy",
                table: "AppUserEventStatusLedgers");

            migrationBuilder.DropColumn(
                name: "EndTime",
                table: "AppUserEvents");

            migrationBuilder.DropColumn(
                name: "EventDkp",
                table: "AppUserEvents");

            migrationBuilder.DropColumn(
                name: "PauseTime",
                table: "AppUserEvents");

            migrationBuilder.DropColumn(
                name: "Proctor",
                table: "AppUserEvents");

            migrationBuilder.DropColumn(
                name: "IsQuickJoin",
                table: "AppUserEventHistories");

            migrationBuilder.DropColumn(
                name: "IsVerified",
                table: "AppUserEventHistories");

            migrationBuilder.DropColumn(
                name: "Proctor",
                table: "AppUserEventHistories");

            migrationBuilder.DropColumn(
                name: "StartTime",
                table: "AppUserEventHistories");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttInputRowCount",
                table: "WindowEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAtUtc",
                table: "WindowEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FirstAttInputRowNumber",
                table: "WindowEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "WindowEvents",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppUserId",
                table: "EventWindowRosterSnapshots",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AttInputAppendedAt",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreationSource",
                table: "Events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscordExtraMessageIds",
                table: "Events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EventDkp",
                table: "Events",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WdAwaitingProcessingSince",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Details",
                table: "EventHistories",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EventDkp",
                table: "EventHistories",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartDate",
                table: "EventHistories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AttInputAppendedAt",
                table: "EventAttendanceWindows",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AttInputAppendedAt",
                table: "AttendanceSnapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "AttendanceSnapshots",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // NOT NULL, so rows that exist on rollback need a value: the capture time is the
            // closest thing to when the row was made.
            migrationBuilder.Sql("UPDATE \"AttendanceSnapshots\" SET \"CreatedAtUtc\" = \"CapturedAtUtc\";");

            migrationBuilder.AddColumn<string>(
                name: "UtcOffset",
                table: "AttendanceSnapshots",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppUserId",
                table: "AppUserEventStatusLedgers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeniedBy",
                table: "AppUserEventStatusLedgers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EventAttendanceWindowId",
                table: "AppUserEventStatusLedgers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "AppUserEventStatusLedgers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedBy",
                table: "AppUserEventStatusLedgers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EndTime",
                table: "AppUserEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "EventDkp",
                table: "AppUserEvents",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PauseTime",
                table: "AppUserEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Proctor",
                table: "AppUserEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsQuickJoin",
                table: "AppUserEventHistories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsVerified",
                table: "AppUserEventHistories",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Proctor",
                table: "AppUserEventHistories",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "StartTime",
                table: "AppUserEventHistories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventStatusLedgers_AppUserId",
                table: "AppUserEventStatusLedgers",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventStatusLedgers_EventAttendanceWindowId",
                table: "AppUserEventStatusLedgers",
                column: "EventAttendanceWindowId");

            migrationBuilder.AddForeignKey(
                name: "FK_AppUserEventStatusLedgers_AspNetUsers_AppUserId",
                table: "AppUserEventStatusLedgers",
                column: "AppUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_AppUserEventStatusLedgers_EventAttendanceWindows_EventAtten~",
                table: "AppUserEventStatusLedgers",
                column: "EventAttendanceWindowId",
                principalTable: "EventAttendanceWindows",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
