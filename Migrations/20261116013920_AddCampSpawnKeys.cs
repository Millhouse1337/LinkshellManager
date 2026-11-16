using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Round 5, R5.2: expand step for one key per spawn on the camp tables.
    //
    // Events, PartySetups, ClaimShieldCaptures and LinkshellMonsterTimings gain SpawnKey: the
    // monster name's one-per-spawn key (HnmConfig.SpawnKey; "Fafnir", "Nidhogg" and
    // "Fafnir/Nidhogg" all give "fafnir/nidhogg"), the same key Tods and HnmRecurringBoards already
    // carry. Events and PartySetups take it from AssignedMonsterName (never EventName: only the
    // assigned monster is a spawn), the other two from MonsterName. FillSql fills old rows here, and
    // the app stamps it on every save (ApplicationDbContext.StampDerivedColumns). Nothing reads the
    // columns yet; R5.3 re-runs the fill (to repair rows an older build saved meanwhile) and then
    // moves the camp, timing and route lookups onto them.
    //
    // Indexes: (LinkshellId, SpawnKey) on Events, ClaimShieldCaptures and LinkshellMonsterTimings.
    // The timings one is NOT unique yet; R5.3 makes it unique, guarded. PartySetups get no index,
    // because nothing looks a setup up by monster. Every existing index stays.
    //
    // The SpawnKey SQL is copied into this file as text (SpawnKeySql below) rather than calling
    // Data/TodDerivedSql, so this migration keeps doing exactly what it did even if that helper
    // changes later. AddCampSpawnKeysRoundTripTests checks the copy gives the same answer as
    // HnmConfig.SpawnKey and TodDerivedSql.SpawnKeySql for every monster name production holds plus
    // the edge cases.
    //
    // Safe to run twice: the fill only writes rows whose stored key differs from the derived one,
    // and says in one NOTICE how many it wrote. No DKP, gil, ledger or loot row is touched, and no
    // row is deleted.
    //
    // Down drops the three indexes and the four columns (the keys are re-derived from the names on
    // the next Up). The build before this one never reads or writes them, so rolling the app back
    // by a folder swap without running Down also works.
    /// <inheritdoc />
    public partial class AddCampSpawnKeys : Migration
    {
        // The key HnmConfig.SpawnKey gives for a monster name, as SQL: blank is NULL; otherwise the
        // pieces between '/' are trimmed (every character .NET calls whitespace, spelled out so it
        // does not depend on the server's locale), empty ones dropped, and lower-cased; a name with
        // either half of a merge pair (in HnmConfig.MonsterMergePairs order) is that pair's combined
        // label, anything else its pieces joined by '/'. A copy of TodDerivedSql.SpawnKeySql at the
        // time of writing. Public so the round-trip test can check it.
        public static string SpawnKeySql(string column) => $$"""
            (SELECT CASE
                WHEN {{column}} IS NULL OR {{column}} !~ '[^ \t\n\v\f\r\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]' THEN NULL
                WHEN 'adamantoise' = ANY(s.l) OR 'aspidochelone' = ANY(s.l) THEN 'adamantoise/aspidochelone'
                WHEN 'behemoth' = ANY(s.l) OR 'king behemoth' = ANY(s.l) THEN 'behemoth/king behemoth'
                WHEN 'fafnir' = ANY(s.l) OR 'nidhogg' = ANY(s.l) THEN 'fafnir/nidhogg'
                ELSE array_to_string(s.l, '/') END
             FROM (SELECT coalesce(array_agg(lower(z.t) ORDER BY z.i), '{}'::text[]) AS l
                   FROM (SELECT regexp_replace(u.s, '^[ \t\n\v\f\r\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+|[ \t\n\v\f\r\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+$', '', 'g') AS t, u.i
                         FROM unnest(string_to_array({{column}}, '/')) WITH ORDINALITY AS u(s, i)) z
                   WHERE z.t <> '') s)
            """;

        // One table's fill: only rows whose stored key differs from the derived one are written.
        private static string FillTableSql(string table, string nameColumn) => $"""
                WITH d AS (SELECT x."Id", {SpawnKeySql($"x.\"{nameColumn}\"")} AS k FROM "{table}" x)
                UPDATE "{table}" x SET "SpawnKey" = d.k
                FROM d
                WHERE x."Id" = d."Id" AND x."SpawnKey" IS DISTINCT FROM d.k;
            """;

        // Public so the round-trip test (and R5.3's switch migration, as a copy) can run exactly
        // this fill again.
        public static readonly string FillSql = $"""
            DO $$
            DECLARE events integer; setups integer; captures integer; timings integer;
            BEGIN
                {FillTableSql("Events", "AssignedMonsterName")}
                GET DIAGNOSTICS events = ROW_COUNT;
                {FillTableSql("PartySetups", "AssignedMonsterName")}
                GET DIAGNOSTICS setups = ROW_COUNT;
                {FillTableSql("ClaimShieldCaptures", "MonsterName")}
                GET DIAGNOSTICS captures = ROW_COUNT;
                {FillTableSql("LinkshellMonsterTimings", "MonsterName")}
                GET DIAGNOSTICS timings = ROW_COUNT;
                RAISE NOTICE 'AddCampSpawnKeys: keyed % event(s), % party setup(s), % claim shield capture(s), % monster timing(s).', events, setups, captures, timings;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SpawnKey",
                table: "PartySetups",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpawnKey",
                table: "LinkshellMonsterTimings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpawnKey",
                table: "Events",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpawnKey",
                table: "ClaimShieldCaptures",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            // Filled before the indexes are built, so each index is built once over the final keys.
            migrationBuilder.Sql(FillSql);

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellMonsterTimings_LinkshellId_SpawnKey",
                table: "LinkshellMonsterTimings",
                columns: new[] { "LinkshellId", "SpawnKey" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_LinkshellId_SpawnKey",
                table: "Events",
                columns: new[] { "LinkshellId", "SpawnKey" });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptures_LinkshellId_SpawnKey",
                table: "ClaimShieldCaptures",
                columns: new[] { "LinkshellId", "SpawnKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LinkshellMonsterTimings_LinkshellId_SpawnKey",
                table: "LinkshellMonsterTimings");

            migrationBuilder.DropIndex(
                name: "IX_Events_LinkshellId_SpawnKey",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_ClaimShieldCaptures_LinkshellId_SpawnKey",
                table: "ClaimShieldCaptures");

            migrationBuilder.DropColumn(
                name: "SpawnKey",
                table: "PartySetups");

            migrationBuilder.DropColumn(
                name: "SpawnKey",
                table: "LinkshellMonsterTimings");

            migrationBuilder.DropColumn(
                name: "SpawnKey",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "SpawnKey",
                table: "ClaimShieldCaptures");
        }
    }
}
