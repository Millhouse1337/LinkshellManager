using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacySkyNmCustomRows : Migration
    {
        // Finishes what RemoveSkyNmMonsterTimings started. That one deleted the eight Sky farm NMs
        // but spared any row flagged IsCustom, on the principle that a row an officer added is their
        // data. Correct in general — wrong for these eight, because they were never added by an
        // officer.
        //
        // They came back through LinkshellMonsterTimingProvisioner.BuildSeed: a linkshell seeded
        // AFTER the Sky category was retired has these names sitting in its legacy
        // Linkshell.TodMonsterTimings blob, they no longer match anything in the built-in catalog,
        // and the blob importer's "not in the catalog = the officer added it" rule files them as
        // custom. So they reappeared wearing the one flag that made every subsequent sweep skip
        // them, on every linkshell whose blob carried them.
        //
        // Matched on NAME and NOT on IsCustom, unlike its predecessor — that filter is precisely
        // what let them survive. Scoped to these eight exactly: it is the same list the earlier
        // migration retired, so this removes nothing that was not already meant to be gone.
        //
        // DELIBERATELY NOT INCLUDED: Lord of Onzozo, which turns up in the same legacy blobs but
        // carries a hand-set 16-hour cooldown rather than the untouched 2-hour default the eight
        // below share. That is someone's real configuration and it stays. Anyone who wants a Sky NM
        // back adds it with "+ Add monster", which now makes it campable too.
        //
        // Every linkshell, by design — there is no LinkshellId filter.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Mirrors RemoveSkyNmMonsterTimings.SkyFarmNms; referenced rather than retyped so the
            // two lists cannot drift. NormalizedMonsterName is the lower-invariant column the unique
            // index is on, so this catches a row however its display name was cased.
            var names = string.Join(
                ", ",
                RemoveSkyNmMonsterTimings.SkyFarmNms.Select(name => $"'{name.ToLowerInvariant()}'"));

            migrationBuilder.Sql($@"
                DELETE FROM ""LinkshellMonsterTimings""
                WHERE ""NormalizedMonsterName"" IN ({names});");
        }

        // Not reversible, for the same reason as its predecessor: Down would have to invent the rows
        // back per linkshell and guess which had been re-timed. Rolling back leaves the editor
        // without them, which is the state a freshly seeded linkshell is in anyway.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
