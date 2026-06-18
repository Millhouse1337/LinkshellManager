using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueAppUserEventHistoryMember : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Self-healing safety net: collapse any duplicate (EventHistoryId, AppUserId)
            // attendance rows to the earliest one BEFORE the unique index is created, so the
            // index can never fail to apply (a failed migration at startup is fatal). The
            // app's invariant means duplicates shouldn't exist, so in practice this is a
            // no-op — it just guarantees the deploy can't crash on unexpected legacy data.
            migrationBuilder.Sql(@"
                DELETE FROM ""AppUserEventHistories"" a
                USING ""AppUserEventHistories"" b
                WHERE a.""AppUserId"" IS NOT NULL
                  AND a.""AppUserId"" = b.""AppUserId""
                  AND a.""EventHistoryId"" = b.""EventHistoryId""
                  AND a.""Id"" > b.""Id"";");

            migrationBuilder.DropIndex(
                name: "IX_AppUserEventHistories_EventHistoryId",
                table: "AppUserEventHistories");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventHistories_EventHistoryId_AppUserId",
                table: "AppUserEventHistories",
                columns: new[] { "EventHistoryId", "AppUserId" },
                unique: true,
                filter: "\"AppUserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppUserEventHistories_EventHistoryId_AppUserId",
                table: "AppUserEventHistories");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventHistories_EventHistoryId",
                table: "AppUserEventHistories",
                column: "EventHistoryId");
        }
    }
}
