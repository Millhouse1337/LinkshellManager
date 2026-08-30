using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AdjustToauHnmCooldowns : Migration
    {
        // Data-only. Cerberus / Hydra / Khimaira were seeded with a 72-hour cooldown, which is when
        // their 25 x 60-min spawn window CLOSES, not when it opens — so a camp ended with a ToD
        // predicted its next pop a full window late, and a Repeat-on-ToD board scheduled to
        // re-post LeadHours BEFORE the pop only came back after the window had already gone by.
        // MonsterTimingDefaults now answers 48h (see HnmConfig.ToauHnms).
        //
        // Seeding is LAZY and all-or-nothing, so the code change alone only reaches linkshells that
        // have never opened the Monster Timings editor. Every linkshell that predates this deploy
        // holds its own row at the old value forever unless it is rewritten here.
        //
        // ONLY rows still sitting on the old default are touched:
        //   - IsCustom = FALSE   — a monster an officer added themselves is their data.
        //   - CooldownMinutes = 4320 — a linkshell that already re-timed these to something else
        //     (48h by hand, or a value tuned to their server) chose that number, and this is not
        //     the place to overrule it. That does mean a linkshell that deliberately set 72h keeps
        //     nothing; there is no way to tell that apart from an untouched seed, and the seed is
        //     overwhelmingly the likelier reading of a row that exactly matches the old default.
        //
        // Mirrors HnmConfig.ToauHnms. Public so a test can hold the two side by side — a name that
        // drifts here leaves a linkshell scheduling that monster a whole window late.
        public static readonly string[] ToauHnms =
        {
            "Cerberus",
            "Hydra",
            "Khimaira",
        };

        // Mirrors MonsterTimingDefaults.ToauCooldownMinutes and the value it replaces. Inlined for
        // the same reason the names are: a migration runs once against real data, and it must keep
        // meaning what it meant on the day it ran even after the constants move on.
        public const int OldCooldownMinutes = 72 * 60;
        public const int NewCooldownMinutes = 48 * 60;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NormalizedMonsterName is the lower-invariant column the unique index is on, so this
            // catches a row however its display name was cased.
            var names = string.Join(", ", ToauHnms.Select(name => $"'{name.ToLowerInvariant()}'"));

            migrationBuilder.Sql($@"
                UPDATE ""LinkshellMonsterTimings""
                SET ""CooldownMinutes"" = {NewCooldownMinutes},
                    ""UpdatedAtUtc"" = NOW() AT TIME ZONE 'UTC'
                WHERE ""IsCustom"" = FALSE
                  AND ""NormalizedMonsterName"" IN ({names})
                  AND ""CooldownMinutes"" = {OldCooldownMinutes};");
        }

        // Reversible, and symmetric with Up: it puts back only the rows Up could have written, so
        // rolling back on a database Up never touched is a no-op rather than a fresh 72h stamp on
        // a linkshell that had chosen 48h itself. IsCustom is checked for the same reason.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var names = string.Join(", ", ToauHnms.Select(name => $"'{name.ToLowerInvariant()}'"));

            migrationBuilder.Sql($@"
                UPDATE ""LinkshellMonsterTimings""
                SET ""CooldownMinutes"" = {OldCooldownMinutes},
                    ""UpdatedAtUtc"" = NOW() AT TIME ZONE 'UTC'
                WHERE ""IsCustom"" = FALSE
                  AND ""NormalizedMonsterName"" IN ({names})
                  AND ""CooldownMinutes"" = {NewCooldownMinutes};");
        }
    }
}
