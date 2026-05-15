using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAttInputAppending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AttendanceSnapshots_Events_LinkedEventId",
                table: "AttendanceSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_PendingAttendanceSnapshotSubmissions_Events_LinkedEventId",
                table: "PendingAttendanceSnapshotSubmissions");

            migrationBuilder.AddColumn<string>(
                name: "AttInputDefaultEntryType",
                table: "Linkshells",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttInputTabName",
                table: "Linkshells",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AttInputAppendedAt",
                table: "Events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttInputEntryType",
                table: "Events",
                type: "character varying(32)",
                maxLength: 32,
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

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceSnapshots_Events_LinkedEventId",
                table: "AttendanceSnapshots",
                column: "LinkedEventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PendingAttendanceSnapshotSubmissions_Events_LinkedEventId",
                table: "PendingAttendanceSnapshotSubmissions",
                column: "LinkedEventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AttendanceSnapshots_Events_LinkedEventId",
                table: "AttendanceSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_PendingAttendanceSnapshotSubmissions_Events_LinkedEventId",
                table: "PendingAttendanceSnapshotSubmissions");

            migrationBuilder.DropColumn(
                name: "AttInputDefaultEntryType",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "AttInputTabName",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "AttInputAppendedAt",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "AttInputEntryType",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "AttInputAppendedAt",
                table: "EventAttendanceWindows");

            migrationBuilder.DropColumn(
                name: "AttInputAppendedAt",
                table: "AttendanceSnapshots");

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceSnapshots_Events_LinkedEventId",
                table: "AttendanceSnapshots",
                column: "LinkedEventId",
                principalTable: "Events",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PendingAttendanceSnapshotSubmissions_Events_LinkedEventId",
                table: "PendingAttendanceSnapshotSubmissions",
                column: "LinkedEventId",
                principalTable: "Events",
                principalColumn: "Id");
        }
    }
}
