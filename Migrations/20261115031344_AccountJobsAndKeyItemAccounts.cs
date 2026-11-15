using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Two "expand" steps (round 4, D5). Both only ADD nullable columns and fill them, so the build
    // before this one, which never reads them, keeps working on this schema after a rollback.
    //
    // 1. The main character's jobs move onto the account. AspNetUsers gets JobLevels, StrongJobs and
    //    MeritJobs (jsonb, the same shapes as the membership columns and the alts' columns). They
    //    used to live only on each membership, written with the same value to every one, so one
    //    character showed the same jobs in every linkshell. The fill copies, for each column on its
    //    own, the value of the account's membership in its primary linkshell, or else of its newest
    //    membership that has one -- the same "primary first" the profile screen reads. Only a JSON
    //    array counts as a value: a jsonb 'null' reads back as "no jobs" in the app, so it must not
    //    win over a real list. The pre-deploy check found one account whose memberships disagree.
    //    The membership columns stay and keep being written, so nothing is lost on a rollback.
    //
    // 2. Key-item ticks get the account of their membership (ChartMemberKeyItems.AppUserId), with a
    //    plain index. Membership ids are not stable (a membership is re-created on unsync/resync),
    //    so a later deploy will read and key the chart by account. A tick whose membership is gone,
    //    has no account, or is in another linkshell stays NULL; that later deploy decides about
    //    those rows. Production has no key-item rows today.
    //
    // Safe to run twice: each fill only touches rows that are still NULL, and says in a NOTICE how
    // many rows it filled. No DKP or gil is touched and no row is deleted.
    //
    // Down drops the new columns and the index. Values written to them since Up are lost; the
    // membership job columns still hold the same jobs, and MembershipId still says whose tick it is.
    public partial class AccountJobsAndKeyItemAccounts : Migration
    {
        // Public so the Postgres round-trip test can run exactly this SQL again.
        public const string FillAccountJobsSql = """
            DO $$
            DECLARE levels integer; strong integer; merits integer;
            BEGIN
                UPDATE "AspNetUsers" u SET "JobLevels" = src.v
                FROM (
                    SELECT DISTINCT ON (m."AppUserId") m."AppUserId", m."JobLevels" AS v
                    FROM "AppUserLinkshells" m
                    JOIN "AspNetUsers" owner ON owner."Id" = m."AppUserId"
                    WHERE jsonb_typeof(m."JobLevels") = 'array'
                    ORDER BY m."AppUserId", (m."LinkshellId" = owner."PrimaryLinkshellId") DESC NULLS LAST, m."Id" DESC
                ) src
                WHERE u."Id" = src."AppUserId" AND u."JobLevels" IS NULL;
                GET DIAGNOSTICS levels = ROW_COUNT;

                UPDATE "AspNetUsers" u SET "StrongJobs" = src.v
                FROM (
                    SELECT DISTINCT ON (m."AppUserId") m."AppUserId", m."StrongJobs" AS v
                    FROM "AppUserLinkshells" m
                    JOIN "AspNetUsers" owner ON owner."Id" = m."AppUserId"
                    WHERE jsonb_typeof(m."StrongJobs") = 'array'
                    ORDER BY m."AppUserId", (m."LinkshellId" = owner."PrimaryLinkshellId") DESC NULLS LAST, m."Id" DESC
                ) src
                WHERE u."Id" = src."AppUserId" AND u."StrongJobs" IS NULL;
                GET DIAGNOSTICS strong = ROW_COUNT;

                UPDATE "AspNetUsers" u SET "MeritJobs" = src.v
                FROM (
                    SELECT DISTINCT ON (m."AppUserId") m."AppUserId", m."MeritJobs" AS v
                    FROM "AppUserLinkshells" m
                    JOIN "AspNetUsers" owner ON owner."Id" = m."AppUserId"
                    WHERE jsonb_typeof(m."MeritJobs") = 'array'
                    ORDER BY m."AppUserId", (m."LinkshellId" = owner."PrimaryLinkshellId") DESC NULLS LAST, m."Id" DESC
                ) src
                WHERE u."Id" = src."AppUserId" AND u."MeritJobs" IS NULL;
                GET DIAGNOSTICS merits = ROW_COUNT;

                RAISE NOTICE 'AccountJobsAndKeyItemAccounts: copied job levels to % account(s), strong jobs to %, merit notes to %.', levels, strong, merits;
            END $$;
            """;

        public const string FillKeyItemAccountsSql = """
            DO $$
            DECLARE filled integer; unmatched integer;
            BEGIN
                UPDATE "ChartMemberKeyItems" k SET "AppUserId" = m."AppUserId"
                FROM "AppUserLinkshells" m
                WHERE m."Id" = k."MembershipId"
                  AND m."LinkshellId" = k."LinkshellId"
                  AND m."AppUserId" IS NOT NULL
                  AND k."AppUserId" IS NULL;
                GET DIAGNOSTICS filled = ROW_COUNT;
                SELECT count(*) INTO unmatched FROM "ChartMemberKeyItems" WHERE "AppUserId" IS NULL;
                RAISE NOTICE 'AccountJobsAndKeyItemAccounts: linked % key-item tick(s) to an account; % have no account.', filled, unmatched;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppUserId",
                table: "ChartMemberKeyItems",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JobLevels",
                table: "AspNetUsers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MeritJobs",
                table: "AspNetUsers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StrongJobs",
                table: "AspNetUsers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.Sql(FillAccountJobsSql);
            migrationBuilder.Sql(FillKeyItemAccountsSql);

            migrationBuilder.CreateIndex(
                name: "IX_ChartMemberKeyItems_AppUserId",
                table: "ChartMemberKeyItems",
                column: "AppUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChartMemberKeyItems_AppUserId",
                table: "ChartMemberKeyItems");

            migrationBuilder.DropColumn(
                name: "AppUserId",
                table: "ChartMemberKeyItems");

            migrationBuilder.DropColumn(
                name: "JobLevels",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "MeritJobs",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StrongJobs",
                table: "AspNetUsers");
        }
    }
}
