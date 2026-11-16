using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Round 5, R5.2: a member's role id (AppUserLinkshells.LinkshellRoleId) becomes required, and
    // Rank becomes an exact copy of that role's name. From this build on, permissions read the id
    // only (LinkshellPermissions); Rank is shown, and the Leader rule reads it, but it is always
    // the role's own name.
    //
    // One DO block, one NOTICE after each step:
    //   1. An empty id is filled from Rank the way the save hook does it (MembershipRoleMatch):
    //      a. the role of the member's own linkshell whose Name equals the trimmed Rank exactly
    //         (capitals matter; "trimmed" means every character .NET calls whitespace);
    //      b. else a Rank that is "leader" in any capitals: the Leader role (permissions already
    //         treat it as the Leader, so it must not become Member);
    //      c. else the Member role: exactly the rights the name fallback gave.
    //   2. Rank is set to the role's name where it differs (" Officer " becomes "Officer").
    //   3. NOT NULL, guarded: if any member still has no role (a linkshell with no Member role),
    //      it says so in a WARNING and leaves the column nullable instead of failing start-up.
    //      Start-up logs the WARNING (MigrationNotices); fix the rows and a later migration sets
    //      NOT NULL. Production had 0 empty ids and 0 Rank/role differences (pre-check C1-C4,
    //      2026-11-15), so every count there should be 0 and NOT NULL is applied.
    //
    // The EF model keeps LinkshellRoleId optional (so this migration changes no model and has an
    // empty snapshot diff). A required model would make every query that loads a member throw if the
    // guard in step 3 ever skipped; see the relationship in ApplicationDbContext.
    //
    // The SQL is literal text in this file, so it keeps doing what it did even if the hook or
    // MembershipRoleMatch change later. MembershipRoleRequiredRoundTripTests checks it gives the
    // role MembershipRoleMatch gives for every kind of Rank.
    //
    // Safe to run twice: each step only writes rows that still need it. No DKP, gil, ledger or loot
    // row is touched, and no row is deleted.
    //
    // Down makes the column nullable again. It does NOT undo step 2 (the Rank text stays the role's
    // exact name) or step 1 (the filled ids stay): both are what the save hook writes anyway. The
    // build before this one always writes a role id, so rolling the app back by a folder swap
    // without running Down also works.
    /// <inheritdoc />
    public partial class MembershipRoleRequired : Migration
    {
        // Every character .NET's string.Trim removes, for a Postgres bracket expression. A copy of
        // TodDerivedSql.WhitespaceChars at the time of writing.
        private const string Ws = @" \t\n\v\f\r\u0085   -     　";

        // Public so the round-trip test can run it again.
        public const string UpSql = $$"""
            DO $$
            DECLARE n integer;
            BEGIN
                -- 1a. The role the trimmed Rank names exactly, in the member's own linkshell.
                UPDATE "AppUserLinkshells" m SET "LinkshellRoleId" = r."Id"
                  FROM "LinkshellRoles" r
                 WHERE m."LinkshellRoleId" IS NULL
                   AND r."LinkshellId" = m."LinkshellId"
                   AND r."Name" = regexp_replace(m."Rank", '^[{{Ws}}]+|[{{Ws}}]+$', '', 'g');
                GET DIAGNOSTICS n = ROW_COUNT;
                RAISE NOTICE 'MembershipRoleRequired: % role id(s) filled from Rank.', n;

                -- 1b. "leader" in any capitals (not trimmed, as LinkshellRanks.IsLeader): the Leader role.
                UPDATE "AppUserLinkshells" m SET "LinkshellRoleId" = r."Id"
                  FROM "LinkshellRoles" r
                 WHERE m."LinkshellRoleId" IS NULL
                   AND m."Rank" ~ '^[Ll][Ee][Aa][Dd][Ee][Rr]$'
                   AND r."LinkshellId" = m."LinkshellId"
                   AND r."Name" = 'Leader';
                GET DIAGNOSTICS n = ROW_COUNT;
                RAISE NOTICE 'MembershipRoleRequired: % membership(s) given the Leader role.', n;

                -- 1c. Anything else still empty: the Member role.
                UPDATE "AppUserLinkshells" m SET "LinkshellRoleId" = r."Id"
                  FROM "LinkshellRoles" r
                 WHERE m."LinkshellRoleId" IS NULL
                   AND r."LinkshellId" = m."LinkshellId"
                   AND r."Name" = 'Member';
                GET DIAGNOSTICS n = ROW_COUNT;
                RAISE NOTICE 'MembershipRoleRequired: % membership(s) given the Member role.', n;

                -- 2. Rank becomes an exact copy of the role name.
                UPDATE "AppUserLinkshells" m SET "Rank" = r."Name"
                  FROM "LinkshellRoles" r
                 WHERE r."Id" = m."LinkshellRoleId"
                   AND m."Rank" IS DISTINCT FROM r."Name";
                GET DIAGNOSTICS n = ROW_COUNT;
                RAISE NOTICE 'MembershipRoleRequired: % Rank text(s) set to the role name.', n;

                -- 3. NOT NULL, guarded.
                SELECT count(*) INTO n FROM "AppUserLinkshells" WHERE "LinkshellRoleId" IS NULL;
                IF n > 0 THEN
                    RAISE WARNING 'MembershipRoleRequired: LinkshellRoleId left nullable: % membership(s) have no role. Run pre-check C3/C4, fix them, and a later migration sets NOT NULL.', n;
                ELSE
                    ALTER TABLE "AppUserLinkshells" ALTER COLUMN "LinkshellRoleId" SET NOT NULL;
                    RAISE NOTICE 'MembershipRoleRequired: LinkshellRoleId is now NOT NULL.';
                END IF;
            END $$;
            """;

        // Dropping NOT NULL from a column that is already nullable (a guarded skip) is not an error.
        public const string DownSql = """
            ALTER TABLE "AppUserLinkshells" ALTER COLUMN "LinkshellRoleId" DROP NOT NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The Rank text rewrite and the filled ids are not undone (see the top of this file).
            migrationBuilder.Sql(DownSql);
        }
    }
}
