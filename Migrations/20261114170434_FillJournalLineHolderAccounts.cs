using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Fills JournalEntryLines.HolderAppUserId on older treasury lines from the holder's name, so
    // "Who's holding the gil" can group them by member (TreasuryBalanceService.ProjectByHolder).
    // Writes since Batch U fill it already (TreasuryJournalWriter); lines from before that only
    // have the name.
    //
    // The name is matched the way the loot-winner backfill matches (RecordLootWinnerAccount):
    // members of the line's own linkshell whose roster name, account name or either alt is that
    // name, case and spaces ignored. Filled only when exactly ONE account matches. A name nobody
    // has, or that two members have, stays NULL and keeps showing as its own name.
    //
    // Safe on live data: only NULLs on gil-on-hand lines are filled, no gil amount changes, and
    // running it twice changes nothing.
    //
    // Down does nothing: the filled-in accounts are correct, and the old build never reads them.
    public partial class FillJournalLineHolderAccounts : Migration
    {
        // Public so the Postgres tests run exactly this SQL against seeded rows.
        public const string BackfillSql = """
            WITH lines AS (
                SELECT l."Id" AS line_id,
                       l."LinkshellId" AS linkshell_id,
                       lower(btrim(l."HolderCharacterName")) AS holder
                FROM "JournalEntryLines" l
                WHERE l."HolderAppUserId" IS NULL
                  AND l."AccountNumber" = 1000
                  AND l."HolderCharacterName" IS NOT NULL
                  AND btrim(l."HolderCharacterName") <> ''
            ),
            matches AS (
                SELECT DISTINCT lines.line_id, m."AppUserId" AS app_user_id
                FROM lines
                JOIN "AppUserLinkshells" m ON m."LinkshellId" = lines.linkshell_id AND m."AppUserId" IS NOT NULL
                JOIN "AspNetUsers" u ON u."Id" = m."AppUserId"
                WHERE lower(btrim(m."CharacterName")) = lines.holder
                   OR lower(btrim(u."CharacterName")) = lines.holder
                   OR lower(btrim(u."AltCharacterName1")) = lines.holder
                   OR lower(btrim(u."AltCharacterName2")) = lines.holder
            ),
            single AS (
                SELECT line_id, MIN(app_user_id) AS app_user_id
                FROM matches
                GROUP BY line_id
                HAVING COUNT(*) = 1
            )
            UPDATE "JournalEntryLines" l
            SET "HolderAppUserId" = single.app_user_id
            FROM single
            WHERE l."Id" = single.line_id;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
