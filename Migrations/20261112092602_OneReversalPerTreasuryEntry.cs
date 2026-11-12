using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// A treasury entry can be reversed only once. A second reversal takes the same gil off the
    /// books twice. TreasuryJournalWriter now refuses it; this unique index stops two reversals
    /// saved at the same moment. Only Reversal rows count: a Fix also writes a Correction that
    /// points at the same entry.
    ///
    /// Created only when no entry is ALREADY reversed twice. Migrations run at startup, and a
    /// plain CREATE UNIQUE INDEX over an old duplicate would fail and crash-loop the app. Deleting
    /// or re-labelling the duplicate would change gil totals or rewrite confirmed history, which
    /// only the owner should decide. So on duplicates it skips the index with a warning, the
    /// writer's check still blocks new ones, and the owner's read-only pre-check lists them.
    /// </summary>
    public partial class OneReversalPerTreasuryEntry : Migration
    {
        // Name and filter match ApplicationDbContext's HasIndex exactly, so the model snapshot and
        // the database agree. Public so the Postgres tests can re-run them over a duplicate.
        public const string CreateIndexSql = """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_JournalEntries_OneReversalPerEntry"
                ON "JournalEntries" ("ReversesJournalEntryId")
                WHERE "Kind" = 'Reversal';
            """;

        // Read-only: every entry reversed more than once, with its reversals. Zero rows means the
        // index was (or can be) created.
        public const string PreCheckSql = """
            SELECT "ReversesJournalEntryId" AS "ReversedEntryId", "Id" AS "ReversalId", "LinkshellId", "EntryNumber", "CreatedAt"
            FROM "JournalEntries"
            WHERE "Kind" = 'Reversal' AND "ReversesJournalEntryId" IN (
                SELECT "ReversesJournalEntryId" FROM "JournalEntries"
                WHERE "Kind" = 'Reversal' AND "ReversesJournalEntryId" IS NOT NULL
                GROUP BY "ReversesJournalEntryId" HAVING count(*) > 1)
            """;

        public const string UpSql = """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "JournalEntries"
                    WHERE "Kind" = 'Reversal' AND "ReversesJournalEntryId" IS NOT NULL
                    GROUP BY "ReversesJournalEntryId" HAVING count(*) > 1)
                THEN
                    RAISE WARNING 'IX_JournalEntries_OneReversalPerEntry not created: a treasury entry is already reversed more than once. Run the pre-check query.';
                ELSE
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_JournalEntries_OneReversalPerEntry"
                        ON "JournalEntries" ("ReversesJournalEntryId")
                        WHERE "Kind" = 'Reversal';
                END IF;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // IF EXISTS: Up may have skipped it.
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_JournalEntries_OneReversalPerEntry";""");
        }
    }
}
