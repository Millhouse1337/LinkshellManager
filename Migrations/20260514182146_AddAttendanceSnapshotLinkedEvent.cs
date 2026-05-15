using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceSnapshotLinkedEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LinkedEventId",
                table: "PendingAttendanceSnapshotSubmissions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinkedEventId",
                table: "AttendanceSnapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingAttendanceSnapshotSubmissions_LinkedEventId",
                table: "PendingAttendanceSnapshotSubmissions",
                column: "LinkedEventId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_LinkedEventId",
                table: "AttendanceSnapshots",
                column: "LinkedEventId");

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

            migrationBuilder.DropIndex(
                name: "IX_PendingAttendanceSnapshotSubmissions_LinkedEventId",
                table: "PendingAttendanceSnapshotSubmissions");

            migrationBuilder.DropIndex(
                name: "IX_AttendanceSnapshots_LinkedEventId",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "LinkedEventId",
                table: "PendingAttendanceSnapshotSubmissions");

            migrationBuilder.DropColumn(
                name: "LinkedEventId",
                table: "AttendanceSnapshots");
        }
    }
}
