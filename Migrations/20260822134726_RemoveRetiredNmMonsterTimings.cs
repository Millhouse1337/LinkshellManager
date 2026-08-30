using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRetiredNmMonsterTimings : Migration
    {
        // Data-only, and the exact twin of RemoveSkyNmMonsterTimings — see that migration for the
        // reasoning, which applies unchanged here.
        //
        // Eight NMs left the built-in catalog (TodManagerViewModel.SupportedMonsters): nobody camped
        // them as events, so they only padded the create-event dropdown and seeded eight rows into
        // every linkshell's monster-setup editor. Seeding is LAZY and ALL-OR-NOTHING — the
        // provisioner only writes when a linkshell has no rows at all — so a linkshell that has ever
        // opened the editor keeps those eight rows forever unless they are removed here.
        //
        // Bloodsucker, King Arthro and King Vinegarroon are deliberately NOT in this list: they stay
        // in the built-in catalog and their rows stay put.
        //
        // BUILT-IN ROWS ONLY. A row an officer added themselves (IsCustom) is their data and is left
        // alone even when it shares one of these names — and after this deploy that is the supported
        // way to have one of these monsters back: add it under Monster setups, where it becomes
        // campable everywhere a built-in is (MonsterTimingMap.EventMonsterOptions / .Allows).
        //
        // Public so a test can hold it against the catalog — a name that stays in both lists would
        // delete a row the seeder immediately rewrites.
        public static readonly string[] RetiredNms =
        {
            "Boroka",
            "Bune",
            "Capricious Cassie",
            "Roc",
            "Serket",
            "Shikigami Weapon",
            "Simurgh",
            "Xolotl",
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NormalizedMonsterName is the lower-invariant column the unique index is on, so this
            // catches a row however its display name was cased.
            var names = string.Join(", ", RetiredNms.Select(name => $"'{name.ToLowerInvariant()}'"));

            migrationBuilder.Sql($@"
                DELETE FROM ""LinkshellMonsterTimings""
                WHERE ""IsCustom"" = FALSE
                  AND ""NormalizedMonsterName"" IN ({names});");
        }

        // Deliberately not reversible, for the same reason as its twin: Down would have to invent
        // eight rows back per linkshell and guess which had been re-timed. Rolling back leaves the
        // catalog without them, which is the state a freshly seeded linkshell is in anyway.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
