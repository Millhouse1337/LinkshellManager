using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class MakeWindowCreditOnePerPerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A person credited twice in one window (two posts that raced before this rule
            // existed) would block the unique index below. Keep the first credit, drop the copies.
            // Harmless to payout: it already counts each person once per window. Nothing else
            // references these rows, and Down cannot bring the copies back.
            migrationBuilder.Sql("""
                DELETE FROM "AppUserEventWindows" dup
                USING "AppUserEventWindows" kept
                WHERE dup."EventAttendanceWindowId" = kept."EventAttendanceWindowId"
                  AND dup."AppUserId" = kept."AppUserId"
                  AND dup."Id" > kept."Id";
                """);

            migrationBuilder.DropIndex(
                name: "IX_AppUserEventWindows_EventAttendanceWindowId_AppUserId",
                table: "AppUserEventWindows");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventWindows_EventAttendanceWindowId_AppUserId",
                table: "AppUserEventWindows",
                columns: new[] { "EventAttendanceWindowId", "AppUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppUserEventWindows_EventAttendanceWindowId_AppUserId",
                table: "AppUserEventWindows");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventWindows_EventAttendanceWindowId_AppUserId",
                table: "AppUserEventWindows",
                columns: new[] { "EventAttendanceWindowId", "AppUserId" });
        }
    }
}
