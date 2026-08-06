using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class BackfillManuallyAddedSnapshotEntries : Migration
    {
        // Data-only. AddedManually only started being recorded when the column was added, so people
        // an officer typed in before that still sort alphabetically into the scanned block and render
        // untinted — the exact rows the feature exists to distinguish.
        //
        // HEURISTIC, and knowingly so: nothing recorded provenance at the time, so this infers it.
        // An addon capture reads its data off the FFXI entity list and comes back with a zone; the
        // add-person form collects a name and nothing else. So "no zone AND no main job AND no sub
        // job" is the shape only a hand-typed row has in practice.
        //
        // It can be wrong: a character the addon saw but could not read at all — no zone, no job —
        // would be relabelled as hand-added. The blast radius of that is entirely cosmetic (the row
        // moves to the bottom and gains a tint); no DKP, credit, or roster membership depends on this
        // flag. An officer can't currently flip it back from the UI, which is the one sharp edge —
        // if that matters, remove this migration and let the flag apply only to new adds.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""AttendanceSnapshotEntries""
                SET ""AddedManually"" = TRUE
                WHERE ""AddedManually"" = FALSE
                  AND (""Zone"" IS NULL OR ""Zone"" = '')
                  AND (""MainJob"" IS NULL OR ""MainJob"" = '')
                  AND (""SubJob"" IS NULL OR ""SubJob"" = '');");
        }

        // Clears everything the guess set. Not a true inverse — it also clears rows that were
        // genuinely hand-added and happen to match the pattern — but since the pattern IS the only
        // evidence available, there is nothing finer to reverse to.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""AttendanceSnapshotEntries""
                SET ""AddedManually"" = FALSE
                WHERE (""Zone"" IS NULL OR ""Zone"" = '')
                  AND (""MainJob"" IS NULL OR ""MainJob"" = '')
                  AND (""SubJob"" IS NULL OR ""SubJob"" = '');");
        }
    }
}
