using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // One-time tidy for the bug HnmEventSeeder.ReviveForNewPopAsync now prevents: a recurring HNM
    // board reuses one Event row for every pop, and its "View Previous Window" captures
    // (EventWindowRosterSnapshots) were never cleared, so each new camp showed the old camps'
    // window rosters mixed in with its own.
    //
    // Only "View Previous Window" reads this table. Review cards, Past Events and the DKP ledger are
    // built from the camp's own attendance rows, so no money or posted card depends on a row
    // deleted here.
    //
    // Deliberately narrow. A board PARKED as defeated (HnmDefeatedAt / WdFinalizedAt set) is left
    // alone: its rows are the camp that just ended, and the board's next revive clears them. On any
    // other HNM board a row goes only when it cannot belong to the camp now on the board:
    //   * the board is QUEUED (not started): the window advancer only captures on a started camp,
    //     so everything here is from an earlier pop;
    //   * the board is RUNNING and the row was captured at or before the board's latest End Camp
    //     (the camp review row End Camp writes, WindowEvents.SourceEventId). This run's captures
    //     all come after that, because the board had to be revived and started again first.
    //     CommencementStartTime is not used as the cut-off: an officer can move a live camp's
    //     start, which would throw away this camp's own early windows.
    //
    // Down() does nothing: the rows were display-only history of camps that are over, and
    // putting them back would only bring the mixed-up rosters back.
    /// <inheritdoc />
    public partial class ClearRecycledBoardWindowRosters : Migration
    {
        // Public so the Postgres tests run exactly this SQL against seeded rows. The owner's
        // read-only pre-check is the same WHERE with SELECT COUNT(*).
        public const string CleanupSql = """
            DELETE FROM "EventWindowRosterSnapshots" s
            USING "Events" e
            WHERE s."EventId" = e."Id"
              AND e."EventType" = 'HNM'
              AND e."HnmDefeatedAt" IS NULL
              AND e."WdFinalizedAt" IS NULL
              AND (
                    e."CommencementStartTime" IS NULL
                    OR s."CapturedAtUtc" <= (
                        SELECT MAX(COALESCE(w."CampEndedAtUtc", w."CreatedAtUtc"))
                        FROM "WindowEvents" w
                        WHERE w."SourceEventId" = e."Id")
                  );
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CleanupSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: see the note above the class.
        }
    }
}
