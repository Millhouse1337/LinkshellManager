using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // The "switch" step for member roles and key-item ticks (round 4, D6). From this deploy the app
    // reads a member's role by id (AppUserLinkshells.LinkshellRoleId) and the key-item grid by
    // account (ChartMemberKeyItems.AppUserId), so both columns must be right before it starts.
    //
    // 1. Role ids are filled again from Rank, which is still what every writer sets. The rule is
    //    exactly the one the app has always used to find a role by name: the role in the same
    //    linkshell whose Name equals the trimmed Rank, case mattering, never a blank name. An id
    //    that no longer matches its Rank (for example a role renamed in Data Admin, or a member
    //    whose Rank an older build changed) is set to NULL, which means "look the role up by name".
    //    So after this, reading by id gives every member exactly the answer reading Rank gives.
    //
    // 2. Each key-item tick gets the account of its membership again, if that membership still
    //    exists in the tick's linkshell and has an account (an account that changed is followed).
    //    Then three kinds of rows that can never be shown are DELETED, each counted in the NOTICE:
    //    a tick with no account, a tick whose account no longer exists, and a second tick of the
    //    same key item for the same account (the one on the member's current membership is kept,
    //    else the oldest). The grid has only ever drawn the current roster, so none of these were on
    //    screen. These rows hold no DKP or gil. Production had no key-item rows at all.
    //
    // 3. The tick's account becomes required, gets a foreign key to AspNetUsers (ON DELETE CASCADE)
    //    and a unique index: one tick per account per key item. The old per-membership unique index
    //    stays until a later deploy drops MembershipId.
    //
    // Safe to run twice: each step only touches rows that are still wrong, and says how many.
    //
    // Down drops the foreign key and the unique index and makes the column nullable again. The
    // deleted ticks are not brought back (they are in the pre-deploy backup), and the role ids stay
    // as filled -- the previous build keeps them in step itself.
    public partial class RosterSwitch : Migration
    {
        // Public so the Postgres round-trip test can run exactly this SQL again.
        public const string RefillRoleIdsSql = """
            DO $$
            DECLARE linked integer; cleared integer; unmatched integer;
            BEGIN
                UPDATE "AppUserLinkshells" m SET "LinkshellRoleId" = r."Id"
                FROM "LinkshellRoles" r
                WHERE r."LinkshellId" = m."LinkshellId"
                  AND r."Name" <> ''
                  AND r."Name" = regexp_replace(m."Rank", '^\s+|\s+$', '', 'g')
                  AND m."LinkshellRoleId" IS DISTINCT FROM r."Id";
                GET DIAGNOSTICS linked = ROW_COUNT;

                UPDATE "AppUserLinkshells" m SET "LinkshellRoleId" = NULL
                WHERE m."LinkshellRoleId" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM "LinkshellRoles" r
                      WHERE r."Id" = m."LinkshellRoleId"
                        AND r."LinkshellId" = m."LinkshellId"
                        AND r."Name" <> ''
                        AND r."Name" = regexp_replace(m."Rank", '^\s+|\s+$', '', 'g'));
                GET DIAGNOSTICS cleared = ROW_COUNT;

                SELECT count(*) INTO unmatched FROM "AppUserLinkshells" WHERE "LinkshellRoleId" IS NULL;
                RAISE NOTICE 'RosterSwitch: set % membership role id(s) from Rank and cleared % stale one(s); % still matched by name.', linked, cleared, unmatched;
            END $$;
            """;

        public const string RefillKeyItemAccountsSql = """
            DO $$
            DECLARE relinked integer; no_account integer; gone_account integer; duplicates integer;
            BEGIN
                UPDATE "ChartMemberKeyItems" k SET "AppUserId" = m."AppUserId"
                FROM "AppUserLinkshells" m
                WHERE m."Id" = k."MembershipId"
                  AND m."LinkshellId" = k."LinkshellId"
                  AND m."AppUserId" IS NOT NULL
                  AND k."AppUserId" IS DISTINCT FROM m."AppUserId";
                GET DIAGNOSTICS relinked = ROW_COUNT;

                DELETE FROM "ChartMemberKeyItems" WHERE "AppUserId" IS NULL;
                GET DIAGNOSTICS no_account = ROW_COUNT;

                DELETE FROM "ChartMemberKeyItems" k
                WHERE NOT EXISTS (SELECT 1 FROM "AspNetUsers" u WHERE u."Id" = k."AppUserId");
                GET DIAGNOSTICS gone_account = ROW_COUNT;

                -- One tick per account per key item: keep the one on the member's current
                -- membership (the previous build reads that one), else the oldest.
                DELETE FROM "ChartMemberKeyItems" WHERE "Id" IN (
                    SELECT ranked."Id" FROM (
                        SELECT k."Id", row_number() OVER (
                            PARTITION BY k."LinkshellId", k."Board", k."KeyItemName", k."AppUserId"
                            ORDER BY (m."Id" IS NOT NULL) DESC, k."Id") AS n
                        FROM "ChartMemberKeyItems" k
                        LEFT JOIN "AppUserLinkshells" m
                          ON m."Id" = k."MembershipId"
                         AND m."LinkshellId" = k."LinkshellId"
                         AND m."AppUserId" = k."AppUserId"
                    ) ranked
                    WHERE ranked.n > 1);
                GET DIAGNOSTICS duplicates = ROW_COUNT;

                RAISE NOTICE 'RosterSwitch: re-linked % key-item tick(s) to their account; deleted % with no account, % whose account is gone and % duplicate(s).', relinked, no_account, gone_account, duplicates;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RefillRoleIdsSql);
            migrationBuilder.Sql(RefillKeyItemAccountsSql);

            migrationBuilder.AlterColumn<string>(
                name: "AppUserId",
                table: "ChartMemberKeyItems",
                type: "character varying(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_AppUserId",
                table: "ChartMemberKeyItems",
                columns: new[] { "LinkshellId", "Board", "KeyItemName", "AppUserId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ChartMemberKeyItems_AspNetUsers_AppUserId",
                table: "ChartMemberKeyItems",
                column: "AppUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChartMemberKeyItems_AspNetUsers_AppUserId",
                table: "ChartMemberKeyItems");

            migrationBuilder.DropIndex(
                name: "IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_AppUserId",
                table: "ChartMemberKeyItems");

            migrationBuilder.AlterColumn<string>(
                name: "AppUserId",
                table: "ChartMemberKeyItems",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(450)",
                oldMaxLength: 450);
        }
    }
}
