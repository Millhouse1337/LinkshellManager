using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class OneCampCreditPerAccount : Migration
    {
        // Folds every account's payout rows for one review card into ONE row, so the unique index
        // below can be created.
        //
        // Where the duplicates come from: before this index, two Posts at the same moment (or a Post
        // racing the old DKP-page sweep) both read "nobody paid yet" and both paid; and before P2.2 a
        // member captured on their main and an alt got one row per name.
        //
        // Money is kept, not removed: the oldest row gets the SUM of the account's rows and the rest
        // are deleted, so every member's balance (LinkshellDkp, a cached sum of the ledger) is exactly
        // what it was. If a camp really did pay someone twice, that stays paid -- correcting it is an
        // officer's call through the DKP audit, not something a migration should decide. An audit
        // correction that pointed at a removed row is moved onto the kept row first, so the audit
        // screen still shows it against the right payout.
        //
        // The same three-step CTE is repeated per statement; only the kept Id and the group key are
        // read after the first step, and those do not change when the kept row's amount does.
        // Public so the Postgres tests run exactly this SQL against seeded duplicates.
        public const string FoldDuplicateCreditsSql = """
            WITH dup AS (
                SELECT "SourceWindowEventId" AS wid, "AppUserId" AS uid, MIN("Id") AS keep_id, SUM("Amount") AS total
                FROM "DkpLedgerEntries"
                WHERE "EntryType" = 'SnapshotEarned' AND "AppUserId" IS NOT NULL AND "SourceWindowEventId" IS NOT NULL
                GROUP BY "SourceWindowEventId", "AppUserId"
                HAVING COUNT(*) > 1)
            UPDATE "DkpLedgerEntries" e
            SET "Amount" = dup.total
            FROM dup
            WHERE e."Id" = dup.keep_id;

            WITH dup AS (
                SELECT "SourceWindowEventId" AS wid, "AppUserId" AS uid, MIN("Id") AS keep_id
                FROM "DkpLedgerEntries"
                WHERE "EntryType" = 'SnapshotEarned' AND "AppUserId" IS NOT NULL AND "SourceWindowEventId" IS NOT NULL
                GROUP BY "SourceWindowEventId", "AppUserId"
                HAVING COUNT(*) > 1)
            UPDATE "DkpLedgerEntries" a
            SET "AuditRelatedLedgerEntryId" = dup.keep_id
            FROM "DkpLedgerEntries" d
            JOIN dup ON d."SourceWindowEventId" = dup.wid AND d."AppUserId" = dup.uid
            WHERE a."AuditRelatedLedgerEntryId" = d."Id"
              AND d."EntryType" = 'SnapshotEarned'
              AND d."Id" <> dup.keep_id;

            WITH dup AS (
                SELECT "SourceWindowEventId" AS wid, "AppUserId" AS uid, MIN("Id") AS keep_id
                FROM "DkpLedgerEntries"
                WHERE "EntryType" = 'SnapshotEarned' AND "AppUserId" IS NOT NULL AND "SourceWindowEventId" IS NOT NULL
                GROUP BY "SourceWindowEventId", "AppUserId"
                HAVING COUNT(*) > 1)
            DELETE FROM "DkpLedgerEntries" e
            USING dup
            WHERE e."SourceWindowEventId" = dup.wid
              AND e."AppUserId" = dup.uid
              AND e."EntryType" = 'SnapshotEarned'
              AND e."Id" <> dup.keep_id;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fold first: the index cannot be created while any duplicate is left. A no-op when
            // there are none.
            migrationBuilder.Sql(FoldDuplicateCreditsSql);

            migrationBuilder.CreateIndex(
                name: "IX_DkpLedgerEntries_SourceWindowEventId_AppUserId",
                table: "DkpLedgerEntries",
                columns: new[] { "SourceWindowEventId", "AppUserId" },
                unique: true,
                filter: "\"EntryType\" = 'SnapshotEarned' AND \"AppUserId\" IS NOT NULL AND \"SourceWindowEventId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the index goes. The folded rows stay folded: they hold the same total, so no
            // balance depends on splitting them again.
            migrationBuilder.DropIndex(
                name: "IX_DkpLedgerEntries_SourceWindowEventId_AppUserId",
                table: "DkpLedgerEntries");
        }
    }
}
