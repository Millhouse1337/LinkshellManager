using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // The "expand" step for characters (round 4, D8), plus one step towards dropping
    // ChartMemberKeyItems.MembershipId.
    //
    // 1. A new Characters table: one row per account per character slot (0 main, 1 alt 1, 2 alt 2),
    //    holding the trimmed name, lower(name) and the slot's job and craft columns. It is filled
    //    here from AspNetUsers, and after every save the app copies the account again
    //    (Data/CharacterSync.cs). Nothing reads it yet; the next deploy switches name lookups to it.
    //    - A blank name has no row. A name longer than 256 characters has no row either, and the
    //      NOTICE counts them (production had none).
    //    - "Blank" and "trimmed" use the whitespace .NET's Trim removes
    //      (AddTodDurationsAndSpawnKeys.WhitespaceChars), so the SQL and the app agree.
    //    - No unique name index: two accounts may share a name (placeholders often do), and so may a
    //      main and an alt of one account. The next deploy decides what to do about those.
    //    - A deleted account takes its rows with it (FK ON DELETE CASCADE), so a claimed placeholder's
    //      rows go when the placeholder is deleted.
    //
    // 2. ChartMemberKeyItems.MembershipId becomes nullable. This build still writes it on every tick
    //    (the build before RosterSwitch reads the grid by it); the next deploy stops writing it and
    //    the one after drops it.
    //
    // Inserts only; nothing is deleted or changed. Safe to run twice (ON CONFLICT DO NOTHING).
    //
    // Down drops the table. Before MembershipId is made required again, a tick with no membership
    // id gets its account's membership in the tick's linkshell; a tick that still has none could not
    // be read by the older build at all, so it is deleted and counted in a NOTICE (no DKP or gil).
    public partial class AddCharacters : Migration
    {
        private const string Ws = AddTodDurationsAndSpawnKeys.WhitespaceChars;

        // One row per account per slot, with the name trimmed. Frozen here on purpose: the app's own
        // copy is CharacterSync.SlotRowsSql, and a Postgres test checks the two give the same rows.
        private const string SlotRowsSql = $$"""
            SELECT u."Id" AS account, s.slot, s.nm, s.jl, s.sj, s.mj, s.cl
            FROM "AspNetUsers" u
            CROSS JOIN LATERAL (VALUES
                (0::smallint, regexp_replace(u."CharacterName", '^[{{Ws}}]+|[{{Ws}}]+$', '', 'g'), u."JobLevels", u."StrongJobs", u."MeritJobs", u."CraftLevels"),
                (1::smallint, regexp_replace(u."AltCharacterName1", '^[{{Ws}}]+|[{{Ws}}]+$', '', 'g'), u."Alt1JobLevels", u."Alt1StrongJobs", u."Alt1MeritJobs", u."Alt1CraftLevels"),
                (2::smallint, regexp_replace(u."AltCharacterName2", '^[{{Ws}}]+|[{{Ws}}]+$', '', 'g'), u."Alt2JobLevels", u."Alt2StrongJobs", u."Alt2MeritJobs", u."Alt2CraftLevels")
            ) AS s(slot, nm, jl, sj, mj, cl)
            """;

        // Public so the Postgres round-trip test can run exactly this SQL again.
        public const string FillSql = $$"""
            DO $$
            DECLARE copied integer; too_long integer;
            BEGIN
                INSERT INTO "Characters" ("AppUserId", "Slot", "Name", "NormalizedName", "JobLevels", "StrongJobs", "MeritJobs", "CraftLevels", "UpdatedAt")
                SELECT s.account, s.slot, s.nm, lower(s.nm), s.jl, s.sj, s.mj, s.cl, now()
                FROM ({{SlotRowsSql}}) s
                WHERE s.nm IS NOT NULL AND s.nm ~ '[^{{Ws}}]' AND char_length(s.nm) <= 256
                ORDER BY s.account, s.slot
                ON CONFLICT ("AppUserId", "Slot") DO NOTHING;
                GET DIAGNOSTICS copied = ROW_COUNT;

                SELECT count(*) INTO too_long FROM ({{SlotRowsSql}}) s WHERE char_length(s.nm) > 256;
                RAISE NOTICE 'AddCharacters: copied % character name(s) from the accounts; left out % longer than 256 characters.', copied, too_long;
            END $$;
            """;

        // Down only. See the class comment.
        public const string RestoreKeyItemMembershipIdsSql = """
            DO $$
            DECLARE restored integer; dropped integer;
            BEGIN
                UPDATE "ChartMemberKeyItems" k SET "MembershipId" = m."Id"
                FROM "AppUserLinkshells" m
                WHERE k."MembershipId" IS NULL
                  AND m."AppUserId" = k."AppUserId"
                  AND m."LinkshellId" = k."LinkshellId"
                  AND NOT EXISTS (
                      SELECT 1 FROM "ChartMemberKeyItems" o
                      WHERE o."LinkshellId" = k."LinkshellId" AND o."Board" = k."Board"
                        AND o."KeyItemName" = k."KeyItemName" AND o."MembershipId" = m."Id");
                GET DIAGNOSTICS restored = ROW_COUNT;

                DELETE FROM "ChartMemberKeyItems" WHERE "MembershipId" IS NULL;
                GET DIAGNOSTICS dropped = ROW_COUNT;
                RAISE NOTICE 'AddCharacters (down): gave % key-item tick(s) their membership id again; deleted % with no membership.', restored, dropped;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "MembershipId",
                table: "ChartMemberKeyItems",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.CreateTable(
                name: "Characters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Slot = table.Column<short>(type: "smallint", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    JobLevels = table.Column<string>(type: "jsonb", nullable: true),
                    StrongJobs = table.Column<string>(type: "jsonb", nullable: true),
                    MeritJobs = table.Column<string>(type: "jsonb", nullable: true),
                    CraftLevels = table.Column<string>(type: "jsonb", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Characters", x => x.Id);
                    table.CheckConstraint("CK_Characters_Name_NotBlank", "\"Name\" ~ '[^ \\t\\n\\v\\f\\r\\u0085\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000]'");
                    table.CheckConstraint("CK_Characters_Slot", "\"Slot\" IN (0, 1, 2)");
                    table.ForeignKey(
                        name: "FK_Characters_AspNetUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Characters_AppUserId_Slot",
                table: "Characters",
                columns: new[] { "AppUserId", "Slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Characters_NormalizedName",
                table: "Characters",
                column: "NormalizedName");

            migrationBuilder.Sql(FillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Characters");

            migrationBuilder.Sql(RestoreKeyItemMembershipIdsSql);

            migrationBuilder.AlterColumn<int>(
                name: "MembershipId",
                table: "ChartMemberKeyItems",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
