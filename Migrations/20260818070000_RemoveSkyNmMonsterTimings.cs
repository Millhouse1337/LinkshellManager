using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSkyNmMonsterTimings : Migration
    {
        // Data-only. The seeded catalog no longer carries a "Sky NMs" heading (see
        // MonsterTimingDefaults), but seeding is LAZY and all-or-nothing — a linkshell that already
        // opened the editor holds its eight Sky rows forever, because the provisioner only writes
        // when a linkshell has none at all. So the rows have to be removed here, or the section the
        // code stopped producing keeps rendering for every linkshell that predates this deploy.
        //
        // BUILT-IN ROWS ONLY. A row an officer added themselves ("IsCustom") is their data even if
        // they filed it under the Sky heading, so it is REFILED to Other NMs rather than deleted —
        // the same landing MonsterTimingDefaults.NormalizeCategory gives an unknown category, applied
        // now so the editor reads right before anyone saves it.
        //
        // Matched on the eight NAMES rather than on Category alone: a built-in Sky row that was
        // already dragged under another heading is still one of these eight and still isn't wanted,
        // and nothing else in the catalog shares a name with them.
        //
        // Mirrors HnmConfig.SkyFarmNmOrder. Public so a test can hold the two side by side — a name
        // that drifts here leaves a row behind on a heading that no longer exists.
        public static readonly string[] SkyFarmNms =
        {
            "Faust",
            "Brigandish Blade",
            "Zipacna",
            "Olla Grande",
            "Steam Cleaner",
            "Mother Globe",
            "Despot",
            "Ullikummi",
        };

        private const string SkyCategory = "Sky NMs";
        private const string OtherCategory = "Other NMs";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NormalizedMonsterName is the lower-invariant column the unique index is on, so this
            // catches a row however its display name was cased.
            var names = string.Join(", ", SkyFarmNms.Select(name => $"'{name.ToLowerInvariant()}'"));

            migrationBuilder.Sql($@"
                DELETE FROM ""LinkshellMonsterTimings""
                WHERE ""IsCustom"" = FALSE
                  AND ""NormalizedMonsterName"" IN ({names});");

            migrationBuilder.Sql($@"
                UPDATE ""LinkshellMonsterTimings""
                SET ""Category"" = '{OtherCategory}'
                WHERE ""Category"" = '{SkyCategory}';");
        }

        // Deliberately not reversible. Down would have to invent the eight rows back for every
        // linkshell, guess which of them the officer had re-timed, and it cannot tell a row it
        // refiled to Other NMs from one that was always there. Rolling back this migration simply
        // leaves the catalog without them — which is exactly the state a linkshell seeded after
        // this deploy is in anyway, so nothing downstream breaks.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
