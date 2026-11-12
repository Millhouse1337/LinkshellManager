using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class FileLootEditLedgerRowsUnderTheirEvent : Migration
    {
        // Loot History's edit refunds/charges and delete refunds were written without the past event
        // they belong to, so deleting that event reversed the loot charge but left the refund behind
        // (the winner ended up richer). New rows carry the event; this files the old ones under it
        // too. Only a label changes -- no amount or balance moves.
        //
        // First statement: from the loot row itself, when it still exists.
        // Second statement: a deleted loot row is gone, but the charge the close wrote for it still
        // names both the loot row and the event, so the event is copied from that sibling -- only
        // when every such sibling agrees on one event that still exists.
        // Public so the Postgres tests run exactly this SQL against seeded rows.
        public const string BackfillSql = """
            UPDATE "DkpLedgerEntries" e
            SET "EventHistoryId" = d."EventHistoryId"
            FROM "EventLootDetails" d
            WHERE e."SourceEventLootDetailId" = d."Id"
              AND e."EventHistoryId" IS NULL
              AND d."EventHistoryId" IS NOT NULL
              AND e."EntryType" IN ('LootEditRefund', 'LootEditSpent', 'LootDeleteRefund');

            UPDATE "DkpLedgerEntries" e
            SET "EventHistoryId" = s.history_id
            FROM (
                SELECT o."SourceEventLootDetailId" AS loot_id, MIN(o."EventHistoryId") AS history_id
                FROM "DkpLedgerEntries" o
                JOIN "EventHistories" h ON h."Id" = o."EventHistoryId"
                WHERE o."SourceEventLootDetailId" IS NOT NULL
                GROUP BY o."SourceEventLootDetailId"
                HAVING COUNT(DISTINCT o."EventHistoryId") = 1
            ) s
            WHERE e."SourceEventLootDetailId" = s.loot_id
              AND e."EventHistoryId" IS NULL
              AND e."EntryType" IN ('LootEditRefund', 'LootEditSpent', 'LootDeleteRefund');
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the event id is only a label on rows that already belonged to that
            // event, and clearing it would also strip it from rows written by the new code.
        }
    }
}
