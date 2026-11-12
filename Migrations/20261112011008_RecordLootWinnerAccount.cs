using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RecordLootWinnerAccount : Migration
    {
        // Fills the new column on existing loot rows from the typed winner name: the member of the
        // row's own linkshell whose main or alt has that name (case and spaces ignored). Only when
        // exactly ONE account matches -- an ambiguous or unknown name is left null, and those rows
        // keep the old name match when they are charged or refunded. No DKP moves here.
        // Public so the Postgres tests run exactly this SQL against seeded rows.
        public const string BackfillSql = """
            WITH loot AS (
                SELECT d."Id" AS loot_id,
                       lower(btrim(d."ItemWinner")) AS winner,
                       COALESCE(d."LinkshellId", e."LinkshellId", h."LinkshellId") AS linkshell_id
                FROM "EventLootDetails" d
                LEFT JOIN "Events" e ON e."Id" = d."EventId"
                LEFT JOIN "EventHistories" h ON h."Id" = d."EventHistoryId"
                WHERE d."ItemWinnerAppUserId" IS NULL
                  AND d."ItemWinner" IS NOT NULL
                  AND btrim(d."ItemWinner") <> ''
            ),
            matches AS (
                SELECT DISTINCT loot.loot_id, m."AppUserId" AS app_user_id
                FROM loot
                JOIN "AppUserLinkshells" m ON m."LinkshellId" = loot.linkshell_id AND m."AppUserId" IS NOT NULL
                JOIN "AspNetUsers" u ON u."Id" = m."AppUserId"
                WHERE lower(btrim(m."CharacterName")) = loot.winner
                   OR lower(btrim(u."CharacterName")) = loot.winner
                   OR lower(btrim(u."AltCharacterName1")) = loot.winner
                   OR lower(btrim(u."AltCharacterName2")) = loot.winner
            ),
            single AS (
                SELECT loot_id, MIN(app_user_id) AS app_user_id
                FROM matches
                GROUP BY loot_id
                HAVING COUNT(*) = 1
            )
            UPDATE "EventLootDetails" d
            SET "ItemWinnerAppUserId" = single.app_user_id
            FROM single
            WHERE d."Id" = single.loot_id;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ItemWinnerAppUserId",
                table: "EventLootDetails",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The winner name is still on every row, so dropping the account loses nothing the old
            // code needs.
            migrationBuilder.DropColumn(
                name: "ItemWinnerAppUserId",
                table: "EventLootDetails");
        }
    }
}
