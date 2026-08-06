using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// Nulls HnmClearedWindow on boards carrying a value from a PREVIOUS pop cycle, so their wyrm
    /// roster starts wiping again.
    ///
    /// HnmClearedWindow is a high-water mark — "every window up to here has had its turnover
    /// settled" — and HnmWindowAdvanceBackgroundService skips the roster wipe while
    /// (HnmClearedWindow ?? 1) >= HnmWindowNumber. HnmEventSeeder.ReviveForNewPopAsync reset the
    /// counter to 1 for the next pop but left this behind, so a repeat-on-ToD Tiamat that reached
    /// window 9 last night came back reading "windows 1-9 already cleared" against a counter at 1:
    /// the new camp marched its window number forward for nine hours over a roster that never
    /// emptied. The revive now nulls it, but that only helps the NEXT recycle — rows already
    /// poisoned are still sitting in the table, which is what this fixes.
    ///
    /// The condition is an impossible state, not a guess: the advancer only ever writes
    /// HnmClearedWindow = HnmWindowNumber, so a healthy row can never have it ahead of the counter.
    /// Anything greater is leftover from a cycle that is over. Scoped to boards still in play
    /// (EndTime / HnmDefeatedAt null) — a finished camp's value is inert either way, and leaving it
    /// keeps the historical record honest.
    /// </summary>
    public partial class HealStaleHnmClearedWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""

                UPDATE "Events"
                SET "HnmClearedWindow" = NULL
                WHERE "HnmClearedWindow" IS NOT NULL
                  AND "HnmClearedWindow" > "HnmWindowNumber"
                  AND "EndTime" IS NULL
                  AND "HnmDefeatedAt" IS NULL;

""");
        }

        /// <inheritdoc />
        // Deliberately a no-op: the values cleared here were stale by definition, and there is
        // nothing to restore them to that wouldn't re-disable the wipe this exists to bring back.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
