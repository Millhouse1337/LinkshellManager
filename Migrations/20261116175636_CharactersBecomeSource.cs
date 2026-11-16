using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Round 5, R5.5: Characters becomes the only written source of names, jobs and crafts
    // (CharacterWriter). Data only: no column, index or constraint changes.
    //
    // 1. Bring Characters in line with the account columns ONE LAST TIME. Until this build the
    //    account columns were the written source and every save (and every start-up) copied them into
    //    Characters, so this should change nothing. It is the previous build's copy
    //    (CharacterSync.DesiredRowsSql and SyncSql), frozen here word for word, run for every account
    //    at once: names trimmed of .NET's whitespace, blank and over-256 names left out, a slot
    //    repeating an earlier slot's name in the same account left out, NormalizedName = lower(Name).
    // 2. AspNetUsers.CharacterName becomes a copy of the main's (slot 0) name, or NULL with no main.
    //    This trims a main with spaces around it (production had none, pre-check A4).
    // 3. The other old columns (alt names, jobs, crafts) become an EXACT copy of Characters, so the
    //    previous build's own copy, if it is swapped back in, finds nothing to change.
    // 4. Every roster name (AppUserLinkshells.CharacterName) becomes its account's CharacterName. The
    //    new build no longer reads it, but the previous build does, so it is kept a copy.
    // Every step writes only rows that differ, and one NOTICE per step says how many it wrote. On
    // production all four should be 0 (pre-check A7, A3, A4 and B2 were 0 on 2026-11-15).
    //
    // Safe to run twice. No DKP, gil, ledger, journal or loot row is touched; the only rows that can be
    // deleted are Characters rows the account columns no longer describe (step 1).
    //
    // Down does nothing: this is a data step on columns that stay. The previous build reads the
    // account columns and the roster names, which this migration leaves equal to Characters, so a
    // rollback by folder swap needs no SQL.
    /// <inheritdoc />
    public partial class CharactersBecomeSource : Migration
    {
        // The whole data step, one DO block so each count can be reported. Public so the round-trip
        // test can run it again by hand.
        public const string FillSql = """
            DO $$
            DECLARE
                removed integer;
                upserted integer;
                mains integer;
                cleared integer;
                mirrored integer;
                rosters integer;
            BEGIN
                -- 1. Characters from the account columns, one last time (the previous build's copy).
                CREATE TEMP TABLE characters_become_source_desired ON COMMIT DROP AS
                SELECT k.account, k.slot, k.nm, k.jl, k.sj, k.mj, k.cl
                FROM (
                    SELECT s.account, s.slot, s.nm, s.jl, s.sj, s.mj, s.cl,
                           row_number() OVER (PARTITION BY s.account, lower(s.nm) ORDER BY s.slot) AS rn
                    FROM (
                        SELECT u."Id" AS account, v.slot, v.nm, v.jl, v.sj, v.mj, v.cl
                        FROM "AspNetUsers" u
                        CROSS JOIN LATERAL (VALUES
                            (0::smallint, regexp_replace(u."CharacterName", '^[ \t\n\v\f\r\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+|[ \t\n\v\f\r\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+$', '', 'g'),
                             u."JobLevels", u."StrongJobs", u."MeritJobs", u."CraftLevels"),
                            (1::smallint, regexp_replace(u."AltCharacterName1", '^[ \t\n\v\f\r\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+|[ \t\n\v\f\r\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+$', '', 'g'),
                             u."Alt1JobLevels", u."Alt1StrongJobs", u."Alt1MeritJobs", u."Alt1CraftLevels"),
                            (2::smallint, regexp_replace(u."AltCharacterName2", '^[ \t\n\v\f\r\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+|[ \t\n\v\f\r\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]+$', '', 'g'),
                             u."Alt2JobLevels", u."Alt2StrongJobs", u."Alt2MeritJobs", u."Alt2CraftLevels")
                        ) AS v(slot, nm, jl, sj, mj, cl)
                    ) s
                    WHERE s.nm IS NOT NULL
                      AND s.nm ~ '[^ \t\n\v\f\r\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]'
                      AND char_length(s.nm) <= 256
                ) k
                WHERE k.rn = 1;

                DELETE FROM "Characters" c
                WHERE NOT EXISTS (
                    SELECT 1 FROM characters_become_source_desired d
                    WHERE d.account = c."AppUserId" AND d.slot = c."Slot" AND lower(d.nm) = c."NormalizedName");
                GET DIAGNOSTICS removed = ROW_COUNT;

                INSERT INTO "Characters" ("AppUserId", "Slot", "Name", "NormalizedName", "JobLevels", "StrongJobs", "MeritJobs", "CraftLevels", "UpdatedAt")
                SELECT d.account, d.slot, d.nm, lower(d.nm), d.jl, d.sj, d.mj, d.cl, now()
                FROM characters_become_source_desired d
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
                GET DIAGNOSTICS upserted = ROW_COUNT;

                DROP TABLE characters_become_source_desired;

                -- 2. The account's CharacterName: the main's name, or NULL with no main.
                UPDATE "AspNetUsers" u SET "CharacterName" = c."Name"
                FROM "Characters" c
                WHERE c."AppUserId" = u."Id" AND c."Slot" = 0 AND u."CharacterName" IS DISTINCT FROM c."Name";
                GET DIAGNOSTICS mains = ROW_COUNT;

                UPDATE "AspNetUsers" u SET "CharacterName" = NULL
                WHERE u."CharacterName" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM "Characters" c WHERE c."AppUserId" = u."Id" AND c."Slot" = 0);
                GET DIAGNOSTICS cleared = ROW_COUNT;

                -- 3. The other old columns: an exact copy of the three slots.
                UPDATE "AspNetUsers" u SET
                    "JobLevels" = s.m_jl, "StrongJobs" = s.m_sj, "MeritJobs" = s.m_mj, "CraftLevels" = s.m_cl,
                    "AltCharacterName1" = s.a1_nm, "Alt1JobLevels" = s.a1_jl, "Alt1StrongJobs" = s.a1_sj,
                    "Alt1MeritJobs" = s.a1_mj, "Alt1CraftLevels" = s.a1_cl,
                    "AltCharacterName2" = s.a2_nm, "Alt2JobLevels" = s.a2_jl, "Alt2StrongJobs" = s.a2_sj,
                    "Alt2MeritJobs" = s.a2_mj, "Alt2CraftLevels" = s.a2_cl
                FROM (
                    SELECT x."Id",
                           m."JobLevels" AS m_jl, m."StrongJobs" AS m_sj, m."MeritJobs" AS m_mj, m."CraftLevels" AS m_cl,
                           a1."Name" AS a1_nm, a1."JobLevels" AS a1_jl, a1."StrongJobs" AS a1_sj, a1."MeritJobs" AS a1_mj, a1."CraftLevels" AS a1_cl,
                           a2."Name" AS a2_nm, a2."JobLevels" AS a2_jl, a2."StrongJobs" AS a2_sj, a2."MeritJobs" AS a2_mj, a2."CraftLevels" AS a2_cl
                    FROM "AspNetUsers" x
                    LEFT JOIN "Characters" m  ON m."AppUserId"  = x."Id" AND m."Slot"  = 0
                    LEFT JOIN "Characters" a1 ON a1."AppUserId" = x."Id" AND a1."Slot" = 1
                    LEFT JOIN "Characters" a2 ON a2."AppUserId" = x."Id" AND a2."Slot" = 2
                ) s
                WHERE s."Id" = u."Id"
                  AND (u."JobLevels", u."StrongJobs", u."MeritJobs", u."CraftLevels",
                       u."AltCharacterName1"::text, u."Alt1JobLevels", u."Alt1StrongJobs", u."Alt1MeritJobs", u."Alt1CraftLevels",
                       u."AltCharacterName2"::text, u."Alt2JobLevels", u."Alt2StrongJobs", u."Alt2MeritJobs", u."Alt2CraftLevels")
                      IS DISTINCT FROM
                      (s.m_jl, s.m_sj, s.m_mj, s.m_cl,
                       s.a1_nm::text, s.a1_jl, s.a1_sj, s.a1_mj, s.a1_cl,
                       s.a2_nm::text, s.a2_jl, s.a2_sj, s.a2_mj, s.a2_cl);
                GET DIAGNOSTICS mirrored = ROW_COUNT;

                -- 4. Roster names: the account's main.
                UPDATE "AppUserLinkshells" l SET "CharacterName" = u."CharacterName"
                FROM "AspNetUsers" u
                WHERE u."Id" = l."AppUserId" AND l."CharacterName" IS DISTINCT FROM u."CharacterName";
                GET DIAGNOSTICS rosters = ROW_COUNT;

                RAISE NOTICE 'CharactersBecomeSource: Characters: % row(s) removed, % row(s) added or updated; CharacterName: % set to the main, % cleared (no main); % account(s) mirrored; % roster name(s) set to the main.',
                    removed, upserted, mains, cleared, mirrored, rosters;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: data only, on columns that stay (see the top of this file).
        }
    }
}
