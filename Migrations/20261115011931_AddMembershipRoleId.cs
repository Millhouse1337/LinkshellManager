using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Memberships get the id of the role their Rank names (AppUserLinkshells.LinkshellRoleId), with
    // a two-column foreign key to (LinkshellRoles.LinkshellId, Id), so a member can only ever hold
    // a role of their own linkshell. This is the "expand" step: the column is added and filled, and
    // the app keeps it filled on every save (ApplicationDbContext.StampMembershipRoleIds), but
    // permissions still read Rank. A later deploy switches them to the id.
    //
    // The fill copies exactly what the app does today when it looks a role up (LinkshellPermissions
    // .GetEffectiveRoleAsync): the role in the same linkshell whose Name equals the trimmed Rank,
    // with case mattering. A blank Rank, a Rank that names no role, or one that only matches with
    // different capitals stays NULL, which means "look it up by name as before" -- so the fill can
    // never give anyone more rights than they have today. The pre-deploy check found every one of
    // production's 252 memberships matches its role exactly.
    //
    // The foreign key is NO ACTION (not RESTRICT), so deleting a linkshell, which cascades to its
    // members and its roles in one statement, still works. Deleting a role someone holds is refused,
    // as the app already refuses it.
    //
    // Safe to run twice: the fill only touches rows that are still NULL, and says in a NOTICE how
    // many it linked and how many are left to match by name. No DKP or gil is touched.
    //
    // Down drops the foreign key, its index, the (LinkshellId, Id) unique key on LinkshellRoles and
    // the column. The old build never reads any of them, so rolling back the app without Down also
    // works.
    public partial class AddMembershipRoleId : Migration
    {
        // Public so the Postgres round-trip test can run exactly this SQL again, and check it gives
        // the same answer as the C# the save hook uses (MembershipRoleMatch).
        public const string FillRoleIdsSql = """
            DO $$
            DECLARE linked integer; unmatched integer;
            BEGIN
                UPDATE "AppUserLinkshells" m SET "LinkshellRoleId" = r."Id"
                FROM "LinkshellRoles" r
                WHERE m."LinkshellRoleId" IS NULL
                  AND r."LinkshellId" = m."LinkshellId"
                  AND r."Name" <> ''
                  AND r."Name" = regexp_replace(m."Rank", '^\s+|\s+$', '', 'g');
                GET DIAGNOSTICS linked = ROW_COUNT;
                SELECT count(*) INTO unmatched FROM "AppUserLinkshells" WHERE "LinkshellRoleId" IS NULL;
                RAISE NOTICE 'AddMembershipRoleId: linked % membership(s) to their role; % still matched by name.', linked, unmatched;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LinkshellRoleId",
                table: "AppUserLinkshells",
                type: "integer",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_LinkshellRoles_LinkshellId_Id",
                table: "LinkshellRoles",
                columns: new[] { "LinkshellId", "Id" });

            migrationBuilder.Sql(FillRoleIdsSql);

            migrationBuilder.CreateIndex(
                name: "IX_AppUserLinkshells_LinkshellId_LinkshellRoleId",
                table: "AppUserLinkshells",
                columns: new[] { "LinkshellId", "LinkshellRoleId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AppUserLinkshells_LinkshellRoles_LinkshellId_LinkshellRoleId",
                table: "AppUserLinkshells",
                columns: new[] { "LinkshellId", "LinkshellRoleId" },
                principalTable: "LinkshellRoles",
                principalColumns: new[] { "LinkshellId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppUserLinkshells_LinkshellRoles_LinkshellId_LinkshellRoleId",
                table: "AppUserLinkshells");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_LinkshellRoles_LinkshellId_Id",
                table: "LinkshellRoles");

            migrationBuilder.DropIndex(
                name: "IX_AppUserLinkshells_LinkshellId_LinkshellRoleId",
                table: "AppUserLinkshells");

            migrationBuilder.DropColumn(
                name: "LinkshellRoleId",
                table: "AppUserLinkshells");
        }
    }
}
