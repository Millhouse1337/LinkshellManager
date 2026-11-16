using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // The "contract" step of Round 5 (R5.7, shipped with R5.6 as "update C"). R5.4 stopped every read
    // and R5.6 every write of these, so this only drops what nothing uses any more
    // (docs/round5-drop-manifest.md):
    // - the 14 old AspNetUsers alt, job and craft columns (AltCharacterName1/2, JobLevels, StrongJobs,
    //   MeritJobs, CraftLevels and the Alt1*/Alt2* versions): old copies of the account's Characters
    //   rows, the only place names, jobs and crafts live since R5.5;
    // - AppUserLinkshells.CharacterName, the roster name: the roster shows the account's main
    //   (AspNetUsers.CharacterName, which STAYS: every save sets it from the main's Characters row).
    // None of them has an index or a constraint.
    //
    // No row is deleted or changed, and no DKP, gil, ledger, journal or loot table is touched.
    // Safe to run twice (IF EXISTS).
    //
    // Down re-adds the columns and fills them from Characters (ReAddSql). A rollback to the update B
    // build (1adf6d6), which still maps the columns, needs RollbackSql run by hand first: see below.
    /// <inheritdoc />
    public partial class Round5Contract : Migration
    {
        public const string MigrationId = "20261116221633_Round5Contract";

        // What Down runs, and the first part of RollbackSql.
        // 1. The 15 columns back, exactly as the update B build maps them (all nullable, so no default
        //    is needed; types from its model snapshot).
        // 2. The "mirror refill": every account's old columns become an exact copy of its Characters
        //    rows (slot 0 the main, 1 and 2 the alts; NULL with no row), CharacterName included, and
        //    every roster name becomes its account's CharacterName (the main). That is exactly what
        //    the update B build keeps them at, so its start-up check ("Characters mirror", "Name
        //    copies") finds 0 differences, and its first save of an account changes nothing. (Names
        //    fit the varchar(64) alt columns: CharacterWriter still takes at most 64 characters.)
        // Only rows that differ are written, and one NOTICE says how many. Safe to run twice.
        public const string ReAddSql = """
            ALTER TABLE "AspNetUsers"
                ADD COLUMN IF NOT EXISTS "AltCharacterName1" character varying(64) NULL,
                ADD COLUMN IF NOT EXISTS "AltCharacterName2" character varying(64) NULL,
                ADD COLUMN IF NOT EXISTS "JobLevels" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "StrongJobs" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "MeritJobs" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "CraftLevels" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "Alt1JobLevels" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "Alt1StrongJobs" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "Alt1MeritJobs" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "Alt1CraftLevels" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "Alt2JobLevels" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "Alt2StrongJobs" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "Alt2MeritJobs" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "Alt2CraftLevels" jsonb NULL;
            ALTER TABLE "AppUserLinkshells" ADD COLUMN IF NOT EXISTS "CharacterName" text NULL;
            DO $$
            DECLARE
                accounts integer;
                rosters integer;
            BEGIN
                UPDATE "AspNetUsers" u SET
                    "CharacterName" = c.m_name,
                    "JobLevels" = c.m_jl, "StrongJobs" = c.m_sj, "MeritJobs" = c.m_mj, "CraftLevels" = c.m_cl,
                    "AltCharacterName1" = c.a1_name, "Alt1JobLevels" = c.a1_jl, "Alt1StrongJobs" = c.a1_sj,
                    "Alt1MeritJobs" = c.a1_mj, "Alt1CraftLevels" = c.a1_cl,
                    "AltCharacterName2" = c.a2_name, "Alt2JobLevels" = c.a2_jl, "Alt2StrongJobs" = c.a2_sj,
                    "Alt2MeritJobs" = c.a2_mj, "Alt2CraftLevels" = c.a2_cl
                FROM (
                    SELECT x."Id" AS id,
                           m."Name" AS m_name, m."JobLevels" AS m_jl, m."StrongJobs" AS m_sj, m."MeritJobs" AS m_mj, m."CraftLevels" AS m_cl,
                           a1."Name" AS a1_name, a1."JobLevels" AS a1_jl, a1."StrongJobs" AS a1_sj, a1."MeritJobs" AS a1_mj, a1."CraftLevels" AS a1_cl,
                           a2."Name" AS a2_name, a2."JobLevels" AS a2_jl, a2."StrongJobs" AS a2_sj, a2."MeritJobs" AS a2_mj, a2."CraftLevels" AS a2_cl
                    FROM "AspNetUsers" x
                    LEFT JOIN "Characters" m  ON m."AppUserId"  = x."Id" AND m."Slot"  = 0
                    LEFT JOIN "Characters" a1 ON a1."AppUserId" = x."Id" AND a1."Slot" = 1
                    LEFT JOIN "Characters" a2 ON a2."AppUserId" = x."Id" AND a2."Slot" = 2
                ) c
                WHERE c.id = u."Id"
                  AND (u."CharacterName", u."JobLevels", u."StrongJobs", u."MeritJobs", u."CraftLevels",
                       u."AltCharacterName1"::text, u."Alt1JobLevels", u."Alt1StrongJobs", u."Alt1MeritJobs", u."Alt1CraftLevels",
                       u."AltCharacterName2"::text, u."Alt2JobLevels", u."Alt2StrongJobs", u."Alt2MeritJobs", u."Alt2CraftLevels")
                      IS DISTINCT FROM
                      (c.m_name::text, c.m_jl, c.m_sj, c.m_mj, c.m_cl,
                       c.a1_name::text, c.a1_jl, c.a1_sj, c.a1_mj, c.a1_cl,
                       c.a2_name::text, c.a2_jl, c.a2_sj, c.a2_mj, c.a2_cl);
                GET DIAGNOSTICS accounts = ROW_COUNT;

                UPDATE "AppUserLinkshells" l SET "CharacterName" = u."CharacterName"
                FROM "AspNetUsers" u
                WHERE u."Id" = l."AppUserId" AND l."CharacterName" IS DISTINCT FROM u."CharacterName";
                GET DIAGNOSTICS rosters = ROW_COUNT;

                RAISE NOTICE 'Round5Contract re-add: % account(s) refilled from Characters; % roster name(s) set to the main.',
                    accounts, rosters;
            END $$;
            """;

        // The owner runs this in psql (after a backup) BEFORE swapping back to the update B build
        // (1adf6d6): that build still maps these columns, so without them every query that loads an
        // account or a membership fails. It is ReAddSql (the columns back, filled from Characters)
        // plus forgetting this migration, so the next update C start runs Up again and the drops
        // happen then. Safe to run twice. Round5ContractRoundTripTests runs exactly this text, and
        // docs/round5-drop-manifest.md holds it word for word.
        public const string RollbackSql = ReAddSql + $"""

            DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '{MigrationId}';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "AspNetUsers"
                    DROP COLUMN IF EXISTS "AltCharacterName1",
                    DROP COLUMN IF EXISTS "AltCharacterName2",
                    DROP COLUMN IF EXISTS "JobLevels",
                    DROP COLUMN IF EXISTS "StrongJobs",
                    DROP COLUMN IF EXISTS "MeritJobs",
                    DROP COLUMN IF EXISTS "CraftLevels",
                    DROP COLUMN IF EXISTS "Alt1JobLevels",
                    DROP COLUMN IF EXISTS "Alt1StrongJobs",
                    DROP COLUMN IF EXISTS "Alt1MeritJobs",
                    DROP COLUMN IF EXISTS "Alt1CraftLevels",
                    DROP COLUMN IF EXISTS "Alt2JobLevels",
                    DROP COLUMN IF EXISTS "Alt2StrongJobs",
                    DROP COLUMN IF EXISTS "Alt2MeritJobs",
                    DROP COLUMN IF EXISTS "Alt2CraftLevels";
                ALTER TABLE "AppUserLinkshells" DROP COLUMN IF EXISTS "CharacterName";
                -- AspNetUsers.CharacterName stays (owner decision 1): the main's name, set by every save.
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ReAddSql);
        }
    }
}
