using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Three "only one" rules the code already follows become unique indexes, so two saves at the
    // same moment cannot break them:
    //
    // - One route per linkshell for each one-owner Discord post type (Loot, Auctions, ToD board,
    //   DKP sheet). Config, so it is fixed automatically: the lowest-Id route keeps the flag and
    //   the others lose it (and their ToD board / DKP sheet message ids, so they repost cleanly).
    // - One open invite per person per linkshell ("Pending" from the old website, "PendingInvite"
    //   from the Activity), by account, and by Discord id for someone with no account yet. Newer
    //   duplicates are deleted; they hold no DKP or gil. Join requests ('PendingJoinRequest') are
    //   not touched: they have their own index (IX_Invites_OneOpenJoinRequest).
    // - One closing window per camp (live event or past event). The close bonus is paid to that
    //   window, so duplicates are NEVER fixed here: if any camp already has two, that index is
    //   skipped with a WARNING in the startup log (the owner fixes the data by hand, and a later
    //   migration adds the index). Kill rosters are left out; they are never the close.
    //
    // The pre-deploy check found none of these duplicates on production, so the clean-up removes
    // nothing there; it stays so the migration also works on a database that has some. Every step
    // says what it did in a NOTICE. Safe to run twice.
    //
    // The code moves these flags in two saves (ChannelRouteEditor, ClosingWindowMark), so the
    // indexes never refuse a correct move.
    //
    // Down drops the indexes. Deleted invites and cleared route flags are not brought back (they
    // are in the pre-deploy dump).
    public partial class OneCloseOneRouteOneInvite : Migration
    {
        // Public so the Postgres round-trip test can run exactly this SQL again.
        public const string ClearExtraRouteOwnersSql = """
            DO $$
            DECLARE cleared integer;
            BEGIN
                UPDATE "LinkshellChannelRoutes" r SET "PostLoot" = false
                WHERE r."PostLoot" AND EXISTS (
                    SELECT 1 FROM "LinkshellChannelRoutes" o
                    WHERE o."LinkshellId" = r."LinkshellId" AND o."PostLoot" AND o."Id" < r."Id");
                GET DIAGNOSTICS cleared = ROW_COUNT;
                RAISE NOTICE 'OneCloseOneRouteOneInvite: cleared Loot on % extra route(s).', cleared;

                UPDATE "LinkshellChannelRoutes" r SET "PostAuctions" = false
                WHERE r."PostAuctions" AND EXISTS (
                    SELECT 1 FROM "LinkshellChannelRoutes" o
                    WHERE o."LinkshellId" = r."LinkshellId" AND o."PostAuctions" AND o."Id" < r."Id");
                GET DIAGNOSTICS cleared = ROW_COUNT;
                RAISE NOTICE 'OneCloseOneRouteOneInvite: cleared Auctions on % extra route(s).', cleared;

                UPDATE "LinkshellChannelRoutes" r SET "PostTodBoard" = false, "TodBoardMessageId" = NULL
                WHERE r."PostTodBoard" AND EXISTS (
                    SELECT 1 FROM "LinkshellChannelRoutes" o
                    WHERE o."LinkshellId" = r."LinkshellId" AND o."PostTodBoard" AND o."Id" < r."Id");
                GET DIAGNOSTICS cleared = ROW_COUNT;
                RAISE NOTICE 'OneCloseOneRouteOneInvite: cleared the ToD board on % extra route(s).', cleared;

                UPDATE "LinkshellChannelRoutes" r
                SET "PostDkpSheet" = false, "DkpSheetMessageId" = NULL, "DkpSheetExtraMessageIds" = NULL
                WHERE r."PostDkpSheet" AND EXISTS (
                    SELECT 1 FROM "LinkshellChannelRoutes" o
                    WHERE o."LinkshellId" = r."LinkshellId" AND o."PostDkpSheet" AND o."Id" < r."Id");
                GET DIAGNOSTICS cleared = ROW_COUNT;
                RAISE NOTICE 'OneCloseOneRouteOneInvite: cleared the DKP sheet on % extra route(s).', cleared;
            END $$;
            """;

        // The oldest open invite stays. Join requests and answered rows are never touched.
        public const string DeleteDuplicateOpenInvitesSql = """
            DO $$
            DECLARE removed integer;
            BEGIN
                DELETE FROM "Invites" i USING "Invites" o
                WHERE i."AppUserId" IS NOT NULL AND i."Status" IN ('Pending', 'PendingInvite')
                  AND o."LinkshellId" = i."LinkshellId" AND o."AppUserId" = i."AppUserId"
                  AND o."Status" IN ('Pending', 'PendingInvite') AND o."Id" < i."Id";
                GET DIAGNOSTICS removed = ROW_COUNT;
                RAISE NOTICE 'OneCloseOneRouteOneInvite: deleted % duplicate open invite(s) by account.', removed;

                DELETE FROM "Invites" i USING "Invites" o
                WHERE i."AppUserId" IS NULL AND i."DiscordUserId" IS NOT NULL AND i."Status" IN ('Pending', 'PendingInvite')
                  AND o."AppUserId" IS NULL AND o."LinkshellId" = i."LinkshellId" AND o."DiscordUserId" = i."DiscordUserId"
                  AND o."Status" IN ('Pending', 'PendingInvite') AND o."Id" < i."Id";
                GET DIAGNOSTICS removed = ROW_COUNT;
                RAISE NOTICE 'OneCloseOneRouteOneInvite: deleted % duplicate open invite(s) by Discord id.', removed;
            END $$;
            """;

        // Names and filters match ApplicationDbContext's HasIndex calls exactly, so the model
        // snapshot and the database agree.
        public const string CreateRouteAndInviteIndexesSql = """
            CREATE UNIQUE INDEX "IX_LinkshellChannelRoutes_OneLootRoute"
                ON "LinkshellChannelRoutes" ("LinkshellId") WHERE "PostLoot";
            CREATE UNIQUE INDEX "IX_LinkshellChannelRoutes_OneAuctionRoute"
                ON "LinkshellChannelRoutes" ("LinkshellId") WHERE "PostAuctions";
            CREATE UNIQUE INDEX "IX_LinkshellChannelRoutes_OneTodBoardRoute"
                ON "LinkshellChannelRoutes" ("LinkshellId") WHERE "PostTodBoard";
            CREATE UNIQUE INDEX "IX_LinkshellChannelRoutes_OneDkpSheetRoute"
                ON "LinkshellChannelRoutes" ("LinkshellId") WHERE "PostDkpSheet";
            CREATE UNIQUE INDEX "IX_Invites_OneOpenInvite"
                ON "Invites" ("LinkshellId", "AppUserId")
                WHERE "Status" IN ('Pending', 'PendingInvite') AND "AppUserId" IS NOT NULL;
            CREATE UNIQUE INDEX "IX_Invites_OneOpenDiscordInvite"
                ON "Invites" ("LinkshellId", "DiscordUserId")
                WHERE "Status" IN ('Pending', 'PendingInvite') AND "AppUserId" IS NULL AND "DiscordUserId" IS NOT NULL;
            """;

        // Read-only: every camp (live or past) with more than one closing window, with its windows.
        // Zero rows means both closing-window indexes were (or can be) created.
        public const string ClosingWindowPreCheckSql = """
            SELECT w."EventId", w."EventHistoryId", w."Id" AS "WindowId", w."SequenceNumber", w."Label", w."PostedAt"
            FROM "EventAttendanceWindows" w
            WHERE w."IsClosingWindow" AND NOT w."IsKillWindow"
              AND (w."EventId" IN (
                       SELECT "EventId" FROM "EventAttendanceWindows"
                       WHERE "IsClosingWindow" AND NOT "IsKillWindow" AND "EventId" IS NOT NULL
                       GROUP BY "EventId" HAVING count(*) > 1)
                OR w."EventHistoryId" IN (
                       SELECT "EventHistoryId" FROM "EventAttendanceWindows"
                       WHERE "IsClosingWindow" AND NOT "IsKillWindow" AND "EventHistoryId" IS NOT NULL
                       GROUP BY "EventHistoryId" HAVING count(*) > 1))
            ORDER BY w."EventId", w."EventHistoryId", w."SequenceNumber"
            """;

        // Each index on its own: duplicates on a past event do not stop the live-camp index.
        public const string CreateClosingWindowIndexesSql = """
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "EventAttendanceWindows"
                    WHERE "IsClosingWindow" AND NOT "IsKillWindow" AND "EventId" IS NOT NULL
                    GROUP BY "EventId" HAVING count(*) > 1)
                THEN
                    RAISE WARNING 'IX_EventAttendanceWindows_OneClosePerEvent not created: a live camp has more than one closing window. Run the pre-check query.';
                ELSE
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_EventAttendanceWindows_OneClosePerEvent"
                        ON "EventAttendanceWindows" ("EventId")
                        WHERE "IsClosingWindow" AND NOT "IsKillWindow";
                END IF;

                IF EXISTS (
                    SELECT 1 FROM "EventAttendanceWindows"
                    WHERE "IsClosingWindow" AND NOT "IsKillWindow" AND "EventHistoryId" IS NOT NULL
                    GROUP BY "EventHistoryId" HAVING count(*) > 1)
                THEN
                    RAISE WARNING 'IX_EventAttendanceWindows_OneClosePerEventHistory not created: a past event has more than one closing window. Run the pre-check query.';
                ELSE
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_EventAttendanceWindows_OneClosePerEventHistory"
                        ON "EventAttendanceWindows" ("EventHistoryId")
                        WHERE "IsClosingWindow" AND NOT "IsKillWindow";
                END IF;
            END $$;
            """;

        public const string DropIndexesSql = """
            DROP INDEX IF EXISTS "IX_LinkshellChannelRoutes_OneLootRoute";
            DROP INDEX IF EXISTS "IX_LinkshellChannelRoutes_OneAuctionRoute";
            DROP INDEX IF EXISTS "IX_LinkshellChannelRoutes_OneTodBoardRoute";
            DROP INDEX IF EXISTS "IX_LinkshellChannelRoutes_OneDkpSheetRoute";
            DROP INDEX IF EXISTS "IX_Invites_OneOpenInvite";
            DROP INDEX IF EXISTS "IX_Invites_OneOpenDiscordInvite";
            DROP INDEX IF EXISTS "IX_EventAttendanceWindows_OneClosePerEvent";
            DROP INDEX IF EXISTS "IX_EventAttendanceWindows_OneClosePerEventHistory";
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ClearExtraRouteOwnersSql);
            migrationBuilder.Sql(DeleteDuplicateOpenInvitesSql);
            migrationBuilder.Sql(CreateRouteAndInviteIndexesSql);
            migrationBuilder.Sql(CreateClosingWindowIndexesSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // IF EXISTS: Up may have skipped a closing-window index.
            migrationBuilder.Sql(DropIndexesSql);
        }
    }
}
