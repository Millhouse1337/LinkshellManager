using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveEventAttendanceWindows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "EventId",
                table: "EventAttendanceWindows",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "EventHistoryId",
                table: "EventAttendanceWindows",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WindowsAttended",
                table: "AppUserEventHistories",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventAttendanceWindows_EventHistoryId_SequenceNumber",
                table: "EventAttendanceWindows",
                columns: new[] { "EventHistoryId", "SequenceNumber" });

            migrationBuilder.AddForeignKey(
                name: "FK_EventAttendanceWindows_EventHistories_EventHistoryId",
                table: "EventAttendanceWindows",
                column: "EventHistoryId",
                principalTable: "EventHistories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EventAttendanceWindows_EventHistories_EventHistoryId",
                table: "EventAttendanceWindows");

            migrationBuilder.DropIndex(
                name: "IX_EventAttendanceWindows_EventHistoryId_SequenceNumber",
                table: "EventAttendanceWindows");

            migrationBuilder.DropColumn(
                name: "EventHistoryId",
                table: "EventAttendanceWindows");

            migrationBuilder.DropColumn(
                name: "WindowsAttended",
                table: "AppUserEventHistories");

            migrationBuilder.AlterColumn<int>(
                name: "EventId",
                table: "EventAttendanceWindows",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
