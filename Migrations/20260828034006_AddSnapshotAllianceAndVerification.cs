using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotAllianceAndVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AllianceNumber",
                table: "AttendanceSnapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostedByAppUserId",
                table: "AttendanceSnapshots",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAtUtc",
                table: "AttendanceSnapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedByAppUserId",
                table: "AttendanceSnapshots",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_LinkshellId_SnapshotStatus",
                table: "AttendanceSnapshots",
                columns: new[] { "LinkshellId", "SnapshotStatus" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AttendanceSnapshots_LinkshellId_SnapshotStatus",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "AllianceNumber",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "PostedByAppUserId",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "VerifiedAtUtc",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "VerifiedByAppUserId",
                table: "AttendanceSnapshots");
        }
    }
}
