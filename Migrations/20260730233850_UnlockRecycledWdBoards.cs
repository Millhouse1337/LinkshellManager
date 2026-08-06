using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// Clears the finalized/claimed/killed state off WD boards that were recycled back into an open
    /// window, so they behave like the fresh boards they now are.
    ///
    /// A recycled Event row keeps its identity, which is the trap: without this, a board that had
    /// already been finalized once came back locked — check-in refused, because the finalizer had
    /// stamped WdFinalizedAt and nothing cleared it on reuse.
    ///
    /// Scoped to boards that are genuinely still live: WD attendance mode, finalized, but neither
    /// defeated (HnmDefeatedAt) nor ended (EndTime). A board that really is over keeps its stamps.
    /// </summary>
    public partial class UnlockRecycledWdBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""

                UPDATE "Events"
                SET "WdFinalizedAt" = NULL,
                    "WdAwaitingProcessingSince" = NULL,
                    "WdPopWindow" = NULL,
                    "WdClaimed" = FALSE,
                    "WdKilled" = FALSE
                WHERE "AttendanceMode" = 'Wd'
                  AND "WdFinalizedAt" IS NOT NULL
                  AND "HnmDefeatedAt" IS NULL
                  AND "EndTime" IS NULL;

""");
        }

        /// <inheritdoc />
        // Deliberately a no-op: the stamps this cleared are not recoverable, and re-locking every
        // recycled board would break check-in again for the linkshells this fixed.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
