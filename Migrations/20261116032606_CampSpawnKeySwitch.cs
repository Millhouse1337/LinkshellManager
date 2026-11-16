using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Round 5, R5.3: switch step for one key per spawn on the camp tables.
    //
    // 1. Re-fill SpawnKey on Events, PartySetups, ClaimShieldCaptures and LinkshellMonsterTimings
    //    (the same fill AddCampSpawnKeys ran). A build older than AddCampSpawnKeys, running after a
    //    rollback, saves these rows without stamping the key, so this repairs any row whose stored
    //    key differs from the one its monster name gives. One NOTICE says how many rows it wrote.
    //    (Start-up runs the same repair after every Migrate as well, SpawnKeyRefill, for rows saved
    //    during a rollback that happens after this migration has already run.)
    // 2. Make IX_LinkshellMonsterTimings_LinkshellId_SpawnKey unique: one Monster Setups row per
    //    SPAWN per linkshell, so "Nidhogg" can no longer sit beside "Fafnir/Nidhogg". Guarded: if
    //    two rows of one linkshell already name one spawn, it keeps the plain index, says so in a
    //    WARNING (start-up logs it, MigrationNotices) and MigrationHealth warns on every start-up
    //    until it is fixed. Production had 0 such linkshells (pre-check E2, 2026-11-15).
    //    NULL keys (a blank name) never clash: Postgres treats NULLs as different.
    //
    // The SQL is literal text in this file (SpawnKeySql is a copy of AddCampSpawnKeys.SpawnKeySql,
    // itself a copy of TodDerivedSql.SpawnKeySql), so this migration keeps doing exactly what it did
    // even if those change later. CampSpawnKeySwitchRoundTripTests checks the copy gives the same
    // key as HnmConfig.SpawnKey.
    //
    // Safe to run twice: the fill only writes rows whose key differs. No DKP, gil, ledger or loot
    // row is touched, and no row is deleted.
    //
    // Down puts the plain (non-unique) index back. The keys stay: AddCampSpawnKeys owns the columns.
    // The build before this one stamps the keys on every save and the one before that never writes
    // them, so a NULL key can never clash; rolling the app back by a folder swap without running Down
    // also works.
    /// <inheritdoc />
    public partial class CampSpawnKeySwitch : Migration
    {
        private const string Index = "IX_LinkshellMonsterTimings_LinkshellId_SpawnKey";

        // The key HnmConfig.SpawnKey gives for a monster name, as SQL. A copy of
        // AddCampSpawnKeys.SpawnKeySql at the time of writing. Public so the round-trip test can
        // check it.
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

        // Step 1. Public so the round-trip test can run it again.
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
                RAISE NOTICE 'CampSpawnKeySwitch: re-keyed % event(s), % party setup(s), % claim shield capture(s), % monster timing(s).', events, setups, captures, timings;
            END $$;
            """;

        // Step 2, guarded. Public so the round-trip test can run it again.
        public const string UniqueIndexSql = $$"""
            DO $$
            DECLARE n integer;
            BEGIN
                SELECT count(*) INTO n FROM (
                    SELECT 1 FROM "LinkshellMonsterTimings"
                    WHERE "SpawnKey" IS NOT NULL
                    GROUP BY "LinkshellId", "SpawnKey"
                    HAVING count(*) > 1) d;
                IF n > 0 THEN
                    RAISE WARNING 'CampSpawnKeySwitch: {{Index}} stays non-unique: % spawn(s) have two or more Monster Setups rows in one linkshell. Run pre-check E2, keep one row per spawn, and a later migration makes it unique.', n;
                ELSE
                    DROP INDEX IF EXISTS "{{Index}}";
                    CREATE UNIQUE INDEX "{{Index}}" ON "LinkshellMonsterTimings" ("LinkshellId", "SpawnKey");
                    RAISE NOTICE 'CampSpawnKeySwitch: {{Index}} is now unique.';
                END IF;
            END $$;
            """;

        // The plain index AddCampSpawnKeys built. Also right after a guarded skip (it is already
        // plain, and is simply built again).
        public const string DownSql = $"""
            DROP INDEX IF EXISTS "{Index}";
            CREATE INDEX "{Index}" ON "LinkshellMonsterTimings" ("LinkshellId", "SpawnKey");
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FillSql);
            migrationBuilder.Sql(UniqueIndexSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The keys are not cleared (see the top of this file).
            migrationBuilder.Sql(DownSql);
        }
    }
}
