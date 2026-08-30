using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RetireGroundNmMonsterTimings : Migration
    {
        // Data-only, and the third and last of its line — see RemoveSkyNmMonsterTimings and
        // RemoveRetiredNmMonsterTimings, whose reasoning applies unchanged here.
        //
        // Those two left Bloodsucker, King Arthro and King Vinegarroon standing as the built-in
        // "Other NMs". They go now, and the heading is empty by design: which NMs a linkshell camps
        // is that linkshell's own answer, and three hardcoded names were a guess every other
        // linkshell had to delete around. What stays built in is the twelve HNMs, whose spawn grids
        // the board and the addon reason about (TodManagerViewModel.SupportedMonsters).
        //
        // Seeding is LAZY and ALL-OR-NOTHING — the provisioner only writes when a linkshell has no
        // rows at all — so a linkshell that has ever opened the editor keeps these three rows
        // forever unless they are removed here.
        //
        // BUILT-IN ROWS ONLY. A row an officer added themselves (IsCustom) is their data and is left
        // alone even when it shares one of these names — the same line RemoveRetiredNmMonsterTimings
        // drew, and the same one that spared Lord of Onzozo from RemoveLegacySkyNmCustomRows. After
        // this deploy, adding one back under Monster setups is the supported way to have it, and it
        // is campable there exactly as a built-in was (MonsterTimingMap.EventMonsterOptions /
        // .Allows).
        //
        // Public so a test can hold it against the catalog — a name in both lists would delete a row
        // the seeder immediately writes back.
        public static readonly string[] RetiredGroundNms =
        {
            "Bloodsucker",
            "King Arthro",
            "King Vinegarroon",
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NormalizedMonsterName is the lower-invariant column the unique index is on, so this
            // catches a row however its display name was cased.
            var names = string.Join(", ", RetiredGroundNms.Select(name => $"'{name.ToLowerInvariant()}'"));

            migrationBuilder.Sql($@"
                DELETE FROM ""LinkshellMonsterTimings""
                WHERE ""IsCustom"" = FALSE
                  AND ""NormalizedMonsterName"" IN ({names});");
        }

        // Deliberately not reversible, like both of its predecessors: Down would have to invent the
        // rows back per linkshell and guess which had been re-timed. Rolling back leaves the catalog
        // without them, which is the state a freshly seeded linkshell is in anyway.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
