using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// Moves HnmClearedWindow onto the PRINTED window scale for camps that are already live, so the
    /// first advancer tick after this deploy doesn't wipe their roster a window early.
    ///
    /// HnmWindowAdvanceBackgroundService used to settle the turnover against Event.HnmWindowNumber
    /// and now settles it against the number the board prints (DiscordEventMessageBuilder
    /// .FocusWindow), which is one higher for the whole of a live camp. That is what makes the
    /// board's number and the roster underneath it move together — including at commencement, where
    /// window 1's single pop chance is spent the instant the camp forms — but it also means a value
    /// written under the old rule reads one low, and the tick after startup would see
    /// "cleared 6 &lt; printing 7" on a camp whose window 6 turnover was already settled and wipe a
    /// roster mid-window.
    ///
    /// Scoped to LIVE boards, which are the only ones that can be misread: a queued or parked board
    /// has its value nulled by HnmEventSeeder.ReviveForNewPopAsync before it runs again, and an
    /// ended camp's value is inert. NULL is left alone in every case — it reads as "nothing settled
    /// yet" on both scales.
    /// </summary>
    public partial class RebaseHnmClearedWindowOntoPrintedWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""

                UPDATE "Events"
                SET "HnmClearedWindow" = "HnmClearedWindow" + 1
                WHERE "HnmClearedWindow" IS NOT NULL
                  AND "EventType" = 'HNM'
                  AND "EndTime" IS NULL
                  AND "CommencementStartTime" IS NOT NULL
                  AND "HnmDefeatedAt" IS NULL;

""");
        }

        /// <inheritdoc />
        // The exact inverse, so rolling back to the counter-scaled advancer leaves the same live
        // camps reading correctly for it.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""

                UPDATE "Events"
                SET "HnmClearedWindow" = "HnmClearedWindow" - 1
                WHERE "HnmClearedWindow" IS NOT NULL
                  AND "EventType" = 'HNM'
                  AND "EndTime" IS NULL
                  AND "CommencementStartTime" IS NOT NULL
                  AND "HnmDefeatedAt" IS NULL;

""");
        }
    }
}
