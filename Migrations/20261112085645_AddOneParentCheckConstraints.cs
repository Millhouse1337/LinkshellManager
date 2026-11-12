using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// Every attendance window and every auction item must belong to exactly one parent: the live
    /// row (Event / Auction) or its closed archive (EventHistory / AuctionHistory). A row with
    /// neither is invisible on every page; a row with both would be counted twice.
    ///
    /// Added NOT VALID on purpose. Migrations run at startup, and a plain ADD CONSTRAINT checks
    /// every existing row: one unexpected old row would fail the migration and crash-loop the app.
    /// NOT VALID still checks every new insert and update from now on; it just skips the rows that
    /// are already there. Once the owner's read-only pre-check finds no bad rows, a one-off
    /// VALIDATE CONSTRAINT marks them fully checked (see the step's owner actions).
    /// </summary>
    public partial class AddOneParentCheckConstraints : Migration
    {
        // Raw SQL because EF's AddCheckConstraint has no NOT VALID option. The names and
        // expressions match ApplicationDbContext's HasCheckConstraint calls exactly, so the model
        // snapshot and the database agree. Public so the Postgres tests can re-run them against a
        // table that already holds a bad row.
        public const string AddWindowConstraintSql = """
            ALTER TABLE "EventAttendanceWindows"
                ADD CONSTRAINT "CK_EventAttendanceWindows_OneParent"
                CHECK (num_nonnulls("EventId", "EventHistoryId") = 1) NOT VALID;
            """;

        public const string AddAuctionItemConstraintSql = """
            ALTER TABLE "AuctionItems"
                ADD CONSTRAINT "CK_AuctionItems_OneParent"
                CHECK (num_nonnulls("AuctionId", "AuctionHistoryId") = 1) NOT VALID;
            """;

        // Read-only: lists every existing row that breaks either rule. The owner runs it on
        // production after deploy; zero rows means the two VALIDATE statements below are safe.
        public const string PreCheckSql = """
            SELECT 'EventAttendanceWindows' AS "Table", "Id", "EventId" AS "LiveParent", "EventHistoryId" AS "ArchiveParent"
            FROM "EventAttendanceWindows" WHERE num_nonnulls("EventId", "EventHistoryId") <> 1
            UNION ALL
            SELECT 'AuctionItems', "Id", "AuctionId", "AuctionHistoryId"
            FROM "AuctionItems" WHERE num_nonnulls("AuctionId", "AuctionHistoryId") <> 1
            """;

        public const string ValidateSql = """
            ALTER TABLE "EventAttendanceWindows" VALIDATE CONSTRAINT "CK_EventAttendanceWindows_OneParent";
            ALTER TABLE "AuctionItems" VALIDATE CONSTRAINT "CK_AuctionItems_OneParent";
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AddWindowConstraintSql);
            migrationBuilder.Sql(AddAuctionItemConstraintSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_EventAttendanceWindows_OneParent",
                table: "EventAttendanceWindows");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AuctionItems_OneParent",
                table: "AuctionItems");
        }
    }
}
