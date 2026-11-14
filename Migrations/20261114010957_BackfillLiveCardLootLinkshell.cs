using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Loot added from a live event's card was saved without EventLootDetails.LinkshellId (fixed in
    // the same commit). This fills it in for those rows, from the live event or the past event the
    // row belongs to: the same UPDATE AddEventLootLinkshellPoolAndDebitStamp ran when the column
    // was added.
    //
    // Safe on live data: it only fills NULLs, it moves no DKP, and a row whose event is gone stays
    // NULL. Running it twice changes nothing. Loot History already finds these rows through their
    // event, so nothing on screen changes.
    //
    // Down does nothing: the filled-in value is correct, and the old build never reads it as NULL.
    public partial class BackfillLiveCardLootLinkshell : Migration
    {
        // Public so the Postgres tests run exactly this SQL against seeded rows.
        public const string BackfillSql = """
            UPDATE "EventLootDetails" AS l
            SET "LinkshellId" = COALESCE(
                (SELECT e."LinkshellId" FROM "Events" e WHERE e."Id" = l."EventId"),
                (SELECT h."LinkshellId" FROM "EventHistories" h WHERE h."Id" = l."EventHistoryId")
            )
            WHERE l."LinkshellId" IS NULL;
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
