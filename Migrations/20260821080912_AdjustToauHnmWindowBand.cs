using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AdjustToauHnmWindowBand : Migration
    {
        // Data-only. Cerberus / Hydra / Khimaira moved off the wyrms' 25 x 60-min band and onto
        // their own: FIVE windows six hours apart. The band still spans the same 24 hours (window 1
        // at the pop, window 5 a day later) — only the bucketing changed, from twenty-five hourly
        // roster reads to five six-hourly ones. HnmConfig.ToauWindowCount /
        // ToauWindowCadenceMinutes are the code side of the same fact.
        //
        // Seeding is LAZY and all-or-nothing (LinkshellMonsterTimingProvisioner writes the defaults
        // only for a linkshell that has NO rows at all), so the code change alone reaches nobody who
        // has ever opened the Monster Timings editor. Every linkshell that predates this deploy
        // holds its own row at 25 x 60 forever unless it is rewritten here.
        //
        // ONLY rows still sitting on the old default are touched:
        //   - IsCustom = FALSE   — a monster an officer added themselves is their data.
        //   - WindowCount = 25 AND WindowCadenceMinutes = 60 — a linkshell that already re-timed
        //     these chose those numbers, and this is not the place to overrule them. Both columns
        //     are checked together so a half-edited row (a custom count on the default cadence) is
        //     left alone rather than half-overwritten.
        //
        // Mirrors HnmConfig.ToauHnms and its two constants. Inlined rather than referenced for the
        // reason every migration inlines: this runs once against real data and must keep meaning
        // what it meant on the day it ran, even after the constants move on. A test holds the two
        // side by side.
        public static readonly string[] ToauHnms =
        {
            "Cerberus",
            "Hydra",
            "Khimaira",
        };

        public const int OldWindowCount = 25;
        public const int OldWindowCadenceMinutes = 60;
        public const int NewWindowCount = 5;
        public const int NewWindowCadenceMinutes = 6 * 60;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            RetimeSetups(migrationBuilder, NewWindowCount, NewWindowCadenceMinutes, OldWindowCount, OldWindowCadenceMinutes);
            RestampQueuedCamps(migrationBuilder, NewWindowCount, NewWindowCadenceMinutes, OldWindowCount, OldWindowCadenceMinutes);
        }

        // Reversible, and symmetric with Up: it puts back only the rows Up could have written, so
        // rolling back on a database Up never touched is a no-op rather than a fresh 25 x 60 stamp
        // on a linkshell that had chosen 5 x 6h itself.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RetimeSetups(migrationBuilder, OldWindowCount, OldWindowCadenceMinutes, NewWindowCount, NewWindowCadenceMinutes);
            RestampQueuedCamps(migrationBuilder, OldWindowCount, OldWindowCadenceMinutes, NewWindowCount, NewWindowCadenceMinutes);
        }

        // The per-linkshell monster setup rows. NormalizedMonsterName is the lower-invariant column
        // the unique index is on, so this catches a row however its display name was cased. The
        // ToAU three are never stored under a merged "Base/Stronger" name — they have no HQ half —
        // so a plain IN over the three names is the whole set.
        private static void RetimeSetups(MigrationBuilder migrationBuilder, int toCount, int toMinutes, int fromCount, int fromMinutes)
        {
            var names = string.Join(", ", ToauHnms.Select(name => $"'{name.ToLowerInvariant()}'"));

            migrationBuilder.Sql($@"
                UPDATE ""LinkshellMonsterTimings""
                SET ""WindowCount"" = {toCount},
                    ""WindowCadenceMinutes"" = {toMinutes},
                    ""UpdatedAtUtc"" = NOW() AT TIME ZONE 'UTC'
                WHERE ""IsCustom"" = FALSE
                  AND ""NormalizedMonsterName"" IN ({names})
                  AND ""WindowCount"" = {fromCount}
                  AND ""WindowCadenceMinutes"" = {fromMinutes};");
        }

        // Camps capture their spawn grid at creation (Event.SpawnWindowCount / SpawnWindowMinutes)
        // and then KEEP it — a live board re-reading the config would jump from "Window 4 of 25" to
        // "of 5" mid-camp and re-measure snapshots already labelled against the old grid. That rule
        // is why this is scoped to camps that have NOT started:
        //   CommencementStartTime IS NULL   — never went live, so no window has ever opened,
        //   EndTime / HnmDefeatedAt / WdFinalizedAt IS NULL — and it is not a settled camp either.
        // A queued board has no windows behind it and no snapshots to re-measure, so there is
        // nothing for the rule to protect; leaving it stamped would instead put a board posted the
        // day before this deploy on a 25-window band its own linkshell no longer uses.
        //
        // The Discord message itself re-renders on its next edit (a signup, a window advance, the
        // auto-start that takes the camp live), which is when the heading picks up the new count.
        private static void RestampQueuedCamps(MigrationBuilder migrationBuilder, int toCount, int toMinutes, int fromCount, int fromMinutes)
        {
            var names = string.Join(", ", ToauHnms.Select(name => $"'{name.ToLowerInvariant()}'"));

            migrationBuilder.Sql($@"
                UPDATE ""Events""
                SET ""SpawnWindowCount"" = {toCount},
                    ""SpawnWindowMinutes"" = {toMinutes}
                WHERE ""CommencementStartTime"" IS NULL
                  AND ""EndTime"" IS NULL
                  AND ""HnmDefeatedAt"" IS NULL
                  AND ""WdFinalizedAt"" IS NULL
                  AND LOWER(TRIM(COALESCE(""AssignedMonsterName"", ''))) IN ({names})
                  AND ""SpawnWindowCount"" = {fromCount}
                  AND ""SpawnWindowMinutes"" = {fromMinutes};");

            // WindowCountOverride carries the SPAWN count on an app-made HNM camp (HnmEventSeeder
            // stamps HnmConfig.EffectiveWindowCount there) and the attendance-POST count on an
            // addon-made one. On the ToAU three those two numbers are the same, so the old value is
            // 25 either way and the new one is 5 either way — which is the only reason it is safe
            // to move it here alongside the stamp. Same queued-only scope, same exact-match guard.
            migrationBuilder.Sql($@"
                UPDATE ""Events""
                SET ""WindowCountOverride"" = {toCount}
                WHERE ""CommencementStartTime"" IS NULL
                  AND ""EndTime"" IS NULL
                  AND ""HnmDefeatedAt"" IS NULL
                  AND ""WdFinalizedAt"" IS NULL
                  AND LOWER(TRIM(COALESCE(""AssignedMonsterName"", ''))) IN ({names})
                  AND ""WindowCountOverride"" = {fromCount};");
        }
    }
}
