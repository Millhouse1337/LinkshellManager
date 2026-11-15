using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // The "switch" step for characters (round 4, D9). From this build on, the name lookups read
    // Characters instead of the account's four name columns, so first the table is made exactly
    // right, then it gets its one-name-per-account rule.
    //
    // 1. Rebuild. An older build running after a rollback does not keep Characters in step (and the
    //    build before AddCharacters had no copy at all), so every account is copied again by the
    //    same rules the app now uses (Data/CharacterSync.cs), frozen here:
    //    - slots whose name is now blank (or longer than 256 characters) lose their row;
    //    - rows whose name changed are replaced; every other column is updated where it differs;
    //    - NEW: a slot that repeats an earlier slot's name in the same account (an alt equal to the
    //      main, two equal alts; ignoring capitals) has no row. The earlier slot already gives the
    //      account that name, so no name lookup changes. The NOTICE counts them.
    //    Only Characters is touched: it is a copy, and the accounts stay the written source.
    //
    // 2. A unique index on (AppUserId, NormalizedName): one row per name per account. After the
    //    rebuild no account can repeat a name, so it is always created; it stays guarded anyway (the
    //    OneCloseOneRouteOneInvite pattern) so a surprise never stops start-up: then it is skipped
    //    with a WARNING and the post-deploy check reports it. Different accounts may still share a
    //    name (placeholders often carry a real player's name).
    //
    // No DKP, gil, ledger, journal, loot or account row is read for money or changed. Safe to run
    // twice: the rebuild changes nothing the second time, and the index is IF NOT EXISTS.
    //
    // Down drops the index and puts back the repeated slots (the AddCharacters rules), so the
    // table is the full copy the older build expects.
    public partial class CharactersSwitch : Migration
    {
        private const string Ws = AddTodDurationsAndSpawnKeys.WhitespaceChars;

        // One row per account per slot, with the name trimmed. Frozen copy of
        // CharacterSync.SlotRowsSql (a Postgres test checks they give the same rows).
        private const string SlotRowsSql = $$"""
            SELECT u."Id" AS account, s.slot, s.nm, s.jl, s.sj, s.mj, s.cl
            FROM "AspNetUsers" u
            CROSS JOIN LATERAL (VALUES
                (0::smallint, regexp_replace(u."CharacterName", '^[{{Ws}}]+|[{{Ws}}]+$', '', 'g'), u."JobLevels", u."StrongJobs", u."MeritJobs", u."CraftLevels"),
                (1::smallint, regexp_replace(u."AltCharacterName1", '^[{{Ws}}]+|[{{Ws}}]+$', '', 'g'), u."Alt1JobLevels", u."Alt1StrongJobs", u."Alt1MeritJobs", u."Alt1CraftLevels"),
                (2::smallint, regexp_replace(u."AltCharacterName2", '^[{{Ws}}]+|[{{Ws}}]+$', '', 'g'), u."Alt2JobLevels", u."Alt2StrongJobs", u."Alt2MeritJobs", u."Alt2CraftLevels")
            ) AS s(slot, nm, jl, sj, mj, cl)
            """;

        private const string KeepSql =
            $$"""(s.nm IS NOT NULL AND s.nm ~ '[^{{Ws}}]' AND char_length(s.nm) <= 256)""";

        // Every kept slot, numbered within its account's names (1 = first slot with that name).
        private const string NumberedSql = $$"""
            SELECT s.account, s.slot, s.nm, s.jl, s.sj, s.mj, s.cl,
                   row_number() OVER (PARTITION BY s.account, lower(s.nm) ORDER BY s.slot) AS rn
            FROM ({{SlotRowsSql}}) s
            WHERE {{KeepSql}}
            """;

        // The rows every account should have. Frozen copy of CharacterSync.DesiredRowsSql.
        private const string DesiredRowsSql = $$"""
            SELECT k.account, k.slot, k.nm, k.jl, k.sj, k.mj, k.cl
            FROM ({{NumberedSql}}) k
            WHERE k.rn = 1
            """;

        // Public so the Postgres round-trip test can run exactly this SQL again.
        public const string RebuildSql = $$"""
            DO $$
            DECLARE removed integer; written integer; repeats integer; too_long integer;
            BEGIN
                DELETE FROM "Characters" c
                WHERE NOT EXISTS (
                    SELECT 1 FROM ({{DesiredRowsSql}}) d
                    WHERE d.account = c."AppUserId" AND d.slot = c."Slot" AND lower(d.nm) = c."NormalizedName");
                GET DIAGNOSTICS removed = ROW_COUNT;

                INSERT INTO "Characters" ("AppUserId", "Slot", "Name", "NormalizedName", "JobLevels", "StrongJobs", "MeritJobs", "CraftLevels", "UpdatedAt")
                SELECT d.account, d.slot, d.nm, lower(d.nm), d.jl, d.sj, d.mj, d.cl, now()
                FROM ({{DesiredRowsSql}}) d
                ORDER BY d.account, d.slot
                ON CONFLICT ("AppUserId", "Slot") DO UPDATE SET
                    "Name" = EXCLUDED."Name",
                    "NormalizedName" = EXCLUDED."NormalizedName",
                    "JobLevels" = EXCLUDED."JobLevels",
                    "StrongJobs" = EXCLUDED."StrongJobs",
                    "MeritJobs" = EXCLUDED."MeritJobs",
                    "CraftLevels" = EXCLUDED."CraftLevels",
                    "UpdatedAt" = EXCLUDED."UpdatedAt"
                WHERE ("Characters"."Name", "Characters"."NormalizedName", "Characters"."JobLevels",
                       "Characters"."StrongJobs", "Characters"."MeritJobs", "Characters"."CraftLevels")
                    IS DISTINCT FROM (EXCLUDED."Name", EXCLUDED."NormalizedName", EXCLUDED."JobLevels",
                       EXCLUDED."StrongJobs", EXCLUDED."MeritJobs", EXCLUDED."CraftLevels");
                GET DIAGNOSTICS written = ROW_COUNT;

                SELECT count(*) INTO repeats FROM ({{NumberedSql}}) k WHERE k.rn > 1;
                SELECT count(*) INTO too_long FROM ({{SlotRowsSql}}) s WHERE char_length(s.nm) > 256;
                RAISE NOTICE 'CharactersSwitch: rebuilt Characters from the accounts: removed % row(s), added or updated % row(s); left out % name(s) that repeat an earlier name of the same account and % longer than 256 characters.', removed, written, repeats, too_long;
            END $$;
            """;

        // Name and columns match ApplicationDbContext's HasIndex exactly (unique, no filter), so the
        // model snapshot and the database agree.
        public const string CreateNameIndexSql = """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "Characters"
                    GROUP BY "AppUserId", "NormalizedName" HAVING count(*) > 1)
                THEN
                    RAISE WARNING 'IX_Characters_AppUserId_NormalizedName not created: an account has two Characters rows with the same name. Name lookups still work; run the post-deploy check.';
                ELSE
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_Characters_AppUserId_NormalizedName"
                        ON "Characters" ("AppUserId", "NormalizedName");
                    RAISE NOTICE 'CharactersSwitch: IX_Characters_AppUserId_NormalizedName is in place.';
                END IF;
            END $$;
            """;

        // Down only: the slots the rebuild left out for repeating a name come back (insert only).
        public const string RestoreRepeatedSlotsSql = $$"""
            DO $$
            DECLARE restored integer;
            BEGIN
                INSERT INTO "Characters" ("AppUserId", "Slot", "Name", "NormalizedName", "JobLevels", "StrongJobs", "MeritJobs", "CraftLevels", "UpdatedAt")
                SELECT s.account, s.slot, s.nm, lower(s.nm), s.jl, s.sj, s.mj, s.cl, now()
                FROM ({{SlotRowsSql}}) s
                WHERE {{KeepSql}}
                ORDER BY s.account, s.slot
                ON CONFLICT ("AppUserId", "Slot") DO NOTHING;
                GET DIAGNOSTICS restored = ROW_COUNT;
                RAISE NOTICE 'CharactersSwitch (down): put back % character row(s) that repeat a name of the same account.', restored;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RebuildSql);
            migrationBuilder.Sql(CreateNameIndexSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // IF EXISTS: Up may have skipped it.
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Characters_AppUserId_NormalizedName\";");
            migrationBuilder.Sql(RestoreRepeatedSlotsSql);
        }
    }
}
