using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class HnmCampAttendanceHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WdProcessingGraceMinutes",
                table: "Linkshells");

            migrationBuilder.AddColumn<DateTime>(
                name: "CampEndedAtUtc",
                table: "WindowEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CampEventLocation",
                table: "WindowEvents",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CampEventType",
                table: "WindowEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CampStartedAtUtc",
                table: "WindowEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceEventId",
                table: "WindowEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppUserId",
                table: "AttendanceSnapshotEntries",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WindowEvents_SourceEventId",
                table: "WindowEvents",
                column: "SourceEventId");

            migrationBuilder.AddForeignKey(
                name: "FK_WindowEvents_Events_SourceEventId",
                table: "WindowEvents",
                column: "SourceEventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Un-stick any camp that was sitting in "Awaiting Processing" when this shipped.
            //
            // WdProcessingBackgroundService was the only thing that finalized those, and it's gone
            // — while WdAwaitingProcessingSince is still what hides the window controls and the End
            // Camp button, so these boards would have no way forward at all. Their check-in roster
            // is intact (the grace deliberately kept it), so clearing the stamp returns them to a
            // normal live camp and the officer can End Camp again: that now hands off to the
            // Attendance System for review, which is where the DKP is owed anyway.
            migrationBuilder.Sql(@"
                UPDATE ""Events""
                SET ""WdAwaitingProcessingSince"" = NULL
                WHERE ""WdAwaitingProcessingSince"" IS NOT NULL
                  AND ""WdFinalizedAt"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WindowEvents_Events_SourceEventId",
                table: "WindowEvents");

            migrationBuilder.DropIndex(
                name: "IX_WindowEvents_SourceEventId",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "CampEndedAtUtc",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "CampEventLocation",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "CampEventType",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "CampStartedAtUtc",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "SourceEventId",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "AppUserId",
                table: "AttendanceSnapshotEntries");

            migrationBuilder.AddColumn<int>(
                name: "WdProcessingGraceMinutes",
                table: "Linkshells",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
