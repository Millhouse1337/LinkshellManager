using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AlignAddonHnmCampsWithAppBoards : Migration
    {
        // Data-only. Backfills AssignedMonsterName (and the day that rides with it) on the HNM camps
        // the LSM addon filed before it sent those as fields — the same fallback
        // AddonApiController.DeriveHnmFromEventName applies at creation time, applied backwards to the
        // boards that are still on screen.
        //
        // That column is what DiscordEventMessageBuilder.UsesWindows keys on, so a camp missing it
        // lost the whole window feature set at once: no "Window N of M" heading, no countdown, no
        // auto-advance, and no bottom button row — which is where "View Previous Window" and
        // "End Camp / Enter ToD" live. It also left the camp with no location, since HnmConfig.ZoneFor
        // had no monster to resolve.
        //
        // WindowCountOverride is deliberately NOT touched. It holds the count of attendance POSTS the
        // camp takes (2 on a king/dragon, 24 on a wyrm) — see Event.WindowCountOverride — and the
        // SPAWN count the board traverses is derived from the monster by
        // DiscordEventMessageBuilder.EffectiveWindowCount rather than stored. Writing the spawn count
        // into that column is exactly the conflation that made a Standard camp ask its officer for
        // seven posts instead of an Open and a Close.
        //
        // Scoped to camps that are still RUNNING (no EndTime, not defeated, not finalized). A settled
        // camp's monster feeds the credit math its payout was computed from, and re-deriving that
        // after the fact would make history disagree with the ledger.

        // The monster label to store, keyed by every name an event could have been filed under.
        // AssignedMonsterName is stored in the COMBINED "Base/Stronger" form (see the comment on
        // HnmConfig.DisplayMonsterName) — the board narrows it to the base half on days below
        // CombinedFromDay, so the combined form is what lets a camp climb back into HQ territory on
        // later days. That's why "Nidhogg" maps to "Fafnir/Nidhogg" rather than to itself.
        //
        // PUBLIC so a test can hold it against HnmConfig: a name that isn't in the catalog writes a
        // monster the board can't resolve, and SQL in a migration runs once, against real data, with
        // nothing watching.
        public static readonly (string Alias, string Stored)[] Monsters =
        {
            // The kings/dragons.
            ("Adamantoise",               "Adamantoise/Aspidochelone"),
            ("Aspidochelone",             "Adamantoise/Aspidochelone"),
            ("Adamantoise/Aspidochelone", "Adamantoise/Aspidochelone"),
            ("Behemoth",                  "Behemoth/King Behemoth"),
            ("King Behemoth",             "Behemoth/King Behemoth"),
            ("Behemoth/King Behemoth",    "Behemoth/King Behemoth"),
            ("Fafnir",                    "Fafnir/Nidhogg"),
            ("Nidhogg",                   "Fafnir/Nidhogg"),
            ("Fafnir/Nidhogg",            "Fafnir/Nidhogg"),

            // The wyrms. No NQ/HQ pair, so they store as-is.
            ("Tiamat",                    "Tiamat"),
            ("Jormungand",                "Jormungand"),
            ("Vrtra",                     "Vrtra"),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (alias, stored) in Monsters)
            {
                BackfillMonster(migrationBuilder, alias, stored);
            }
        }

        // Deliberately empty. Up writes a value that is DERIVED — the monster the camp's own name
        // already said it was — so there is nothing to restore: the pre-migration value (null) was
        // the bug. Nothing records which rows Up touched either, so a "revert" would have to guess,
        // and guessing wrong here re-breaks a live board's window row. Rolling this back leaves the
        // healed camps healed.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }

        // Reads the monster back out of the camp's own name, e.g. "Behemoth/King Behemoth D1". Mirrors
        // AddonApiController.DeriveHnmFromEventName, which is the live path for the same fallback.
        //
        // Only fills a name that is MISSING — a camp that already carries a monster keeps it, including
        // one deliberately stored as a single half. The day comes off the " D<n>" suffix the addon
        // appends, and is likewise only filled when absent.
        private static void BackfillMonster(MigrationBuilder migrationBuilder, string alias, string stored)
        {
            migrationBuilder.Sql($@"
                UPDATE ""Events""
                SET ""AssignedMonsterName"" = '{Literal(stored)}',
                    ""DayNumber"" = COALESCE(
                        ""DayNumber"",
                        NULLIF(substring(""EventName"" from ' [Dd]([0-9]+)$'), '')::int)
                WHERE ""CreationSource"" = 'Addon'
                  AND ""EventType"" ILIKE 'HNM'
                  AND ""AssignedMonsterName"" IS NULL
                  AND ""EndTime"" IS NULL
                  AND ""HnmDefeatedAt"" IS NULL
                  AND ""WdFinalizedAt"" IS NULL
                  AND (""EventName"" ILIKE '{Literal(alias)}'
                       OR ""EventName"" ILIKE '{Literal(alias)} D%');");
        }

        // ILIKE with no wildcards is case-insensitive equality that respects collation, the same
        // comparison the monster lookups use in code (HnmConfig keys every set OrdinalIgnoreCase).
        // No monster name here contains a % or _, so nothing becomes a wildcard by accident; the only
        // intentional wildcard is the trailing D% on the day suffix. The escape is here for the same
        // reason its twin in RefileSeaPopItemsOntoTheirDroppers is — an unescaped quote would close the
        // literal early and fail to parse on the one run this ever gets.
        private static string Literal(string value) => value.Replace("'", "''");
    }
}
