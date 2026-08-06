using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// Converts every legacy RevenueEntries row into a treasury entry with two balanced halves.
    ///
    /// INSERT-ONLY, into tables that AddTreasuryLedger created empty. RevenueEntries is read and
    /// left completely untouched — it stays readable for a full release so rolling the app binary
    /// back is a non-event, and RemoveRevenueEntries is a separate, later migration. That matters
    /// because Database.Migrate() runs at application startup (Program.cs), so the rollback path for
    /// a bad deploy is "ship the old binary", not "restore a snapshot".
    ///
    /// There is deliberately NO reconciliation gate in here. A RAISE EXCEPTION inside a migration
    /// that runs at startup is not a failed deploy step — it is an app in a restart loop until
    /// someone hand-patches production. Verification is a read-only pre-flight query against a
    /// snapshot instead:
    ///
    ///     SELECT "EntryType", "Category", sign("Value"), count(*), sum("Value")
    ///     FROM "RevenueEntries" GROUP BY 1, 2, 3;
    ///
    /// and afterwards, per linkshell:
    ///
    ///     old = SUM(CASE WHEN "EntryType" = 'Expense' THEN -"Value" ELSE "Value" END)
    ///     new = SUM of JournalEntryLines.Amount on the gil-on-hand category
    ///
    /// RevenueEntryConversionMapTests pins that those two agree for every value in the wild.
    ///
    /// Replayable: every statement is guarded, and IX_JournalEntries_LegacyRevenueEntryId is unique,
    /// so if this aborts halfway you re-run it rather than restoring anything.
    /// </summary>
    public partial class ConvertRevenueEntriesToJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Seed every linkshell's categories. The lazy LedgerAccountProvisioner has not run
            //    yet and the conversion needs category ids to write halves against. Generated from
            //    LedgerAccountDefaults so a converted linkshell reads identically to a fresh one.
            migrationBuilder.Sql($"""
                INSERT INTO "LedgerAccounts"
                    ("LinkshellId", "AccountNumber", "Name", "Description",
                     "IsCash", "IsPostable", "IsSystem", "IsActive", "SortOrder", "CreatedAt")
                SELECT ls."Id", seed.number, seed.name, seed.description,
                       seed.is_cash, seed.is_postable, TRUE, TRUE, seed.sort_order, now()
                FROM "Linkshells" AS ls
                CROSS JOIN (VALUES
                        {RevenueEntryConversionMap.SeedAccountsValuesSql()}
                    ) AS seed(number, name, description, is_cash, is_postable, sort_order)
                WHERE NOT EXISTS (
                    SELECT 1 FROM "LedgerAccounts" AS existing
                    WHERE existing."LinkshellId" = ls."Id"
                      AND existing."AccountNumber" = seed.number
                );
                """);

            // 2. One treasury entry per legacy row.
            //
            //    Recorded as Confirmed: this is history, and history is not a draft. Kind and Source
            //    are both 'Migration' so converted rows stay identifiable forever, alongside the
            //    permanent Legacy* columns that keep the original strings.
            //
            //    The Auction Payout exception matters: those rows put the auction WINNER's name in
            //    CreatedByCharacterName (Auctions.cs / AuctionController.Mutations.cs), not the
            //    officer's. Copying it as the recorder would fabricate an attribution, so it is
            //    dropped here and the winner is written to the half's counterparty in step 3 —
            //    which is where it belonged all along.
            migrationBuilder.Sql($"""
                WITH converted AS (
                    SELECT src."Id" AS legacy_id,
                           src."LinkshellId" AS linkshell_id,
                           src."LinkshellName" AS linkshell_name,
                           src."EntryType" AS legacy_entry_type,
                           src."Category" AS legacy_category,
                           src."Details" AS details,
                           src."OccurredAt" AS occurred_at,
                           src."CreatedAt" AS created_at,
                           src."CreatedByAppUserId" AS created_by_app_user_id,
                           src."CreatedByCharacterName" AS created_by_character_name,
                           lower(btrim(coalesce(src."EntryType", ''))) = 'auction payout' AS is_payout,
                           {RevenueEntryConversionMap.TransactionKindCaseSql()} AS txn_kind,
                           row_number() OVER (PARTITION BY src."LinkshellId" ORDER BY src."OccurredAt", src."Id")
                               + coalesce((
                                   SELECT max(existing."Sequence") FROM "JournalEntries" AS existing
                                   WHERE existing."LinkshellId" = src."LinkshellId"
                               ), 0) AS seq
                    FROM "RevenueEntries" AS src
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "JournalEntries" AS already
                        WHERE already."LegacyRevenueEntryId" = src."Id"
                    )
                )
                INSERT INTO "JournalEntries"
                    ("LinkshellId", "LinkshellName", "Sequence", "EntryNumber", "TransactionDate",
                     "Status", "Kind", "Source", "TransactionKind", "Memo",
                     "CreatedAt", "CreatedByAppUserId", "CreatedByCharacterName",
                     "ConfirmedAt", "ConfirmedByAppUserId", "ConfirmedByCharacterName",
                     "LegacyRevenueEntryId", "LegacyEntryType", "LegacyCategory")
                SELECT c.linkshell_id,
                       c.linkshell_name,
                       c.seq,
                       lpad(c.seq::text, 6, '0'),
                       c.occurred_at,
                       '{JournalEntryStatuses.Confirmed}',
                       '{JournalEntryKinds.Migration}',
                       '{JournalEntrySources.Migration}',
                       c.txn_kind,
                       c.details,
                       c.created_at,
                       c.created_by_app_user_id,
                       CASE WHEN c.is_payout THEN NULL ELSE c.created_by_character_name END,
                       c.created_at,
                       c.created_by_app_user_id,
                       CASE WHEN c.is_payout THEN NULL ELSE c.created_by_character_name END,
                       c.legacy_id,
                       left(c.legacy_entry_type, 32),
                       left(c.legacy_category, 128)
                FROM converted AS c;
                """);

            // 3. The two halves. Line 1 adds to a category, line 2 takes the same amount from
            //    another, so every entry sums to zero (INV-2) by construction.
            //
            //    The amount is always ABS(Value) and the DIRECTION lives in which category is which
            //    — never in the sign. ABS rather than negation is load-bearing: an already-negative
            //    Expense row can exist in production, and negating one would move gil the wrong way.
            //
            //    A row genuinely recorded at 0 gil (both mark-sold paths clamp a negative price to 0)
            //    produces two zero halves. Kept, not dropped: the fact that a sale was recorded is
            //    itself information.
            //
            //    The original free-text Category is preserved on both halves, so nothing the officer
            //    typed is lost even where it could not be mapped to a category.
            migrationBuilder.Sql($"""
                WITH converted AS (
                    SELECT src."Id" AS legacy_id,
                           src."CreatedByCharacterName" AS created_by_character_name,
                           src."Category" AS legacy_category,
                           abs(src."Value") AS amount,
                           lower(btrim(coalesce(src."EntryType", ''))) = 'auction payout' AS is_payout,
                           {RevenueEntryConversionMap.AddToCaseSql()} AS add_to,
                           {RevenueEntryConversionMap.TakeFromCaseSql()} AS take_from
                    FROM "RevenueEntries" AS src
                ),
                halves AS (SELECT 1 AS line_number UNION ALL SELECT 2)
                INSERT INTO "JournalEntryLines"
                    ("JournalEntryId", "LinkshellId", "LedgerAccountId", "AccountNumber", "AccountName",
                     "Amount", "TransactionDate", "LineNumber", "LineMemo",
                     "CounterpartyAppUserId", "CounterpartyCharacterName")
                SELECT je."Id",
                       je."LinkshellId",
                       acct."Id",
                       acct."AccountNumber",
                       acct."Name",
                       CASE WHEN h.line_number = 1 THEN c.amount ELSE -c.amount END,
                       je."TransactionDate",
                       h.line_number,
                       left(c.legacy_category, 256),
                       NULL,
                       CASE WHEN c.is_payout AND h.line_number = 1
                            THEN left(c.created_by_character_name, 256) END
                FROM "JournalEntries" AS je
                JOIN converted AS c ON c.legacy_id = je."LegacyRevenueEntryId"
                CROSS JOIN halves AS h
                JOIN "LedgerAccounts" AS acct
                  ON acct."LinkshellId" = je."LinkshellId"
                 AND acct."AccountNumber" = CASE WHEN h.line_number = 1 THEN c.add_to ELSE c.take_from END
                WHERE je."LegacyRevenueEntryId" IS NOT NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM "JournalEntryLines" AS already
                      WHERE already."JournalEntryId" = je."Id"
                  );
                """);

            // 4. Point sold items at their new entry. Items.RevenueEntryId stays populated too, so a
            //    binary rollback still finds what it expects; the old column goes with RevenueEntries.
            migrationBuilder.Sql("""
                UPDATE "Items" AS item
                SET "JournalEntryId" = je."Id"
                FROM "JournalEntries" AS je
                WHERE je."LegacyRevenueEntryId" = item."RevenueEntryId"
                  AND item."RevenueEntryId" IS NOT NULL
                  AND item."JournalEntryId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Safe to reverse precisely, because every converted row is tagged and RevenueEntries was
            // never modified. Deleting the headers cascades their halves away.
            migrationBuilder.Sql("""
                UPDATE "Items" SET "JournalEntryId" = NULL
                WHERE "JournalEntryId" IN (
                    SELECT "Id" FROM "JournalEntries" WHERE "LegacyRevenueEntryId" IS NOT NULL
                );
                """);
            migrationBuilder.Sql("""
                DELETE FROM "JournalEntries" WHERE "LegacyRevenueEntryId" IS NOT NULL;
                """);
            // Seeded categories are left in place: they are also created lazily by
            // LedgerAccountProvisioner on any treasury read, so removing them here would achieve
            // nothing beyond a moment of churn.
        }
    }
}
