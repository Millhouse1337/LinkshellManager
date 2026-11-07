using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class OneClaimShieldCapturePerCamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Fold the copies every officer's addon made of one lottery into a single capture, so
            // the unique indexes below can be built. Per live camp AND per past event, so ended
            // camps read the same as new ones. The oldest capture of each stays.
            //
            // Payout is unchanged: End Camp already paid everyone named on ANY of a camp's
            // captures once, and the folded capture names the same people. Where two copies
            // disagree on a name, the MATCHED row is kept, since that is the one that gets paid.
            //
            // EventId and EventHistoryId are never both set, so no capture is in two groups.
            // Down cannot split the captures apart again.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "ClaimShieldFold" AS
                SELECT "Id" AS "DupId", "KeepId"
                FROM (
                    SELECT "Id", MIN("Id") OVER (PARTITION BY "EventId") AS "KeepId"
                    FROM "ClaimShieldCaptures" WHERE "EventId" IS NOT NULL
                    UNION ALL
                    SELECT "Id", MIN("Id") OVER (PARTITION BY "EventHistoryId") AS "KeepId"
                    FROM "ClaimShieldCaptures" WHERE "EventHistoryId" IS NOT NULL
                ) grouped
                WHERE "Id" <> "KeepId";

                -- The kept capture takes the fullest read of the lottery (same rule as the post).
                UPDATE "ClaimShieldCaptures" kept
                SET "Won" = kept."Won" OR copies."AnyWon",
                    "TotalPlayers" = GREATEST(kept."TotalPlayers", copies."MostPlayers"),
                    "CapturedMessage" = COALESCE(kept."CapturedMessage", copies."AnyMessage")
                FROM (
                    SELECT fold."KeepId",
                           bool_or(dup."Won") AS "AnyWon",
                           MAX(dup."TotalPlayers") AS "MostPlayers",
                           MIN(dup."CapturedMessage") AS "AnyMessage"
                    FROM "ClaimShieldFold" fold
                    JOIN "ClaimShieldCaptures" dup ON dup."Id" = fold."DupId"
                    GROUP BY fold."KeepId"
                ) copies
                WHERE kept."Id" = copies."KeepId";

                -- Every member moves onto the kept capture...
                UPDATE "ClaimShieldCaptureMembers" moved
                SET "CaptureId" = fold."KeepId"
                FROM "ClaimShieldFold" fold
                WHERE moved."CaptureId" = fold."DupId";

                -- ...then each person keeps ONE row: the same account, or the same name, is the
                -- same person. A row goes when a better one exists for it: for an account, the
                -- older row; for a name, a matched row beats an unmatched one, then the older.
                -- The best row of every person never has a better one, so it always stays.
                DELETE FROM "ClaimShieldCaptureMembers" r
                WHERE EXISTS (
                    SELECT 1 FROM "ClaimShieldCaptureMembers" k
                    WHERE k."CaptureId" = r."CaptureId"
                      AND k."Id" <> r."Id"
                      AND (
                            (r."AppUserId" IS NOT NULL AND k."AppUserId" = r."AppUserId" AND k."Id" < r."Id")
                         OR (lower(k."CharacterName") = lower(r."CharacterName")
                             AND ((k."Matched" AND NOT r."Matched")
                                  OR (k."Matched" = r."Matched" AND k."Id" < r."Id")))
                      )
                );

                DELETE FROM "ClaimShieldCaptures" capture
                USING "ClaimShieldFold" fold
                WHERE capture."Id" = fold."DupId";

                UPDATE "ClaimShieldCaptures" capture
                SET "MemberCount" = (SELECT COUNT(*) FROM "ClaimShieldCaptureMembers" m
                                     WHERE m."CaptureId" = capture."Id"),
                    "MatchedCount" = (SELECT COUNT(*) FROM "ClaimShieldCaptureMembers" m
                                      WHERE m."CaptureId" = capture."Id" AND m."Matched");

                DROP TABLE "ClaimShieldFold";
                """);

            migrationBuilder.DropIndex(
                name: "IX_ClaimShieldCaptures_EventId",
                table: "ClaimShieldCaptures");

            migrationBuilder.DropIndex(
                name: "IX_ClaimShieldCaptureMembers_CaptureId",
                table: "ClaimShieldCaptureMembers");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptures_EventId",
                table: "ClaimShieldCaptures",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptureMembers_CaptureId_AppUserId",
                table: "ClaimShieldCaptureMembers",
                columns: new[] { "CaptureId", "AppUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptureMembers_CaptureId_CharacterName",
                table: "ClaimShieldCaptureMembers",
                columns: new[] { "CaptureId", "CharacterName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClaimShieldCaptures_EventId",
                table: "ClaimShieldCaptures");

            migrationBuilder.DropIndex(
                name: "IX_ClaimShieldCaptureMembers_CaptureId_AppUserId",
                table: "ClaimShieldCaptureMembers");

            migrationBuilder.DropIndex(
                name: "IX_ClaimShieldCaptureMembers_CaptureId_CharacterName",
                table: "ClaimShieldCaptureMembers");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptures_EventId",
                table: "ClaimShieldCaptures",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptureMembers_CaptureId",
                table: "ClaimShieldCaptureMembers",
                column: "CaptureId");
        }
    }
}
