using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class OneParticipationPerEvent : Migration
    {
        // Merges every account's extra participations in one event into its OLDEST row, so the unique
        // index below can be created.
        //
        // Where the duplicates come from: a double-clicked sign-up, or a website join racing an addon
        // post. The board then showed the member twice, and close paid only one of the rows.
        //
        // The oldest row is kept as it is: end-event already paid the FIRST row it met for an account
        // (and skipped the rest), so its timing, break state and Absent mark are what a member was paid
        // on before this rule. What hangs off the extra rows is moved onto it:
        //   * the windows they were credited for (AppUserEventWindows), so a windowed close counts
        //     them. A window credited on both rows is kept once (it pays once anyway);
        //   * their break / verify history (AppUserEventStatusLedgers);
        //   * a Manual Check In arrival (and its check-out) when the kept row has none, because the
        //     Manual Check In finalizer credited the first row that HAD an arrival;
        //   * a name, job or Discord id the kept row is missing.
        // Then the extra rows are deleted. Rows without an account (outside sign-ups) are left alone.
        //
        // A no-op when there are no duplicates. Public so the Postgres tests run exactly this SQL
        // against seeded duplicates.

        // Every extra participation (old_id) and the oldest row of its account in that event (keep_id).
        private const string DupCte = """
            dup AS (
                SELECT s."Id" AS old_id, s.keep_id
                FROM (
                    SELECT "Id", MIN("Id") OVER (PARTITION BY "EventId", "AppUserId") AS keep_id
                    FROM "AppUserEvents"
                    WHERE "AppUserId" IS NOT NULL) s
                WHERE s."Id" <> s.keep_id)
            """;

        public const string MergeDuplicateParticipationsSql = $"""
            WITH grp AS (
                SELECT MIN("Id") AS keep_id,
                       (array_agg("CharacterName" ORDER BY "Id") FILTER (WHERE "CharacterName" IS NOT NULL))[1] AS character_name,
                       (array_agg("JobName" ORDER BY "Id") FILTER (WHERE "JobName" IS NOT NULL))[1] AS job_name,
                       (array_agg("SubJobName" ORDER BY "Id") FILTER (WHERE "SubJobName" IS NOT NULL))[1] AS sub_job_name,
                       (array_agg("JobType" ORDER BY "Id") FILTER (WHERE "JobType" IS NOT NULL))[1] AS job_type,
                       (array_agg("DiscordUserId" ORDER BY "Id") FILTER (WHERE "DiscordUserId" IS NOT NULL))[1] AS discord_user_id
                FROM "AppUserEvents"
                WHERE "AppUserId" IS NOT NULL
                GROUP BY "EventId", "AppUserId"
                HAVING COUNT(*) > 1)
            UPDATE "AppUserEvents" p
            SET "CharacterName" = grp.character_name,
                "JobName" = grp.job_name,
                "SubJobName" = grp.sub_job_name,
                "JobType" = grp.job_type,
                "DiscordUserId" = grp.discord_user_id
            FROM grp
            WHERE p."Id" = grp.keep_id;

            WITH {DupCte},
            wd AS (
                SELECT DISTINCT ON (dup.keep_id) dup.keep_id, x."WdArrivalWindow", x."WdDepartureWindow"
                FROM dup
                JOIN "AppUserEvents" x ON x."Id" = dup.old_id
                WHERE x."WdArrivalWindow" IS NOT NULL
                ORDER BY dup.keep_id, x."Id")
            UPDATE "AppUserEvents" p
            SET "WdArrivalWindow" = wd."WdArrivalWindow",
                "WdDepartureWindow" = wd."WdDepartureWindow"
            FROM wd
            WHERE p."Id" = wd.keep_id AND p."WdArrivalWindow" IS NULL;

            WITH {DupCte},
            ranked AS (
                SELECT w."Id",
                       ROW_NUMBER() OVER (
                           PARTITION BY w."EventAttendanceWindowId", COALESCE(dup.keep_id, w."AppUserEventId")
                           ORDER BY (dup.old_id IS NULL) DESC, w."Id") AS rn
                FROM "AppUserEventWindows" w
                LEFT JOIN dup ON dup.old_id = w."AppUserEventId"
                WHERE w."AppUserEventId" IS NOT NULL)
            DELETE FROM "AppUserEventWindows" w
            USING ranked
            WHERE w."Id" = ranked."Id" AND ranked.rn > 1;

            WITH {DupCte}
            UPDATE "AppUserEventWindows" w SET "AppUserEventId" = dup.keep_id
            FROM dup WHERE w."AppUserEventId" = dup.old_id;

            WITH {DupCte}
            UPDATE "AppUserEventStatusLedgers" l SET "AppUserEventId" = dup.keep_id
            FROM dup WHERE l."AppUserEventId" = dup.old_id;

            WITH {DupCte}
            DELETE FROM "AppUserEvents" p
            USING dup
            WHERE p."Id" = dup.old_id;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Merge first: the index cannot be created while any duplicate is left.
            migrationBuilder.Sql(MergeDuplicateParticipationsSql);

            // The new index starts with EventId, so it also serves every "participants of this
            // event" read the old single-column index did.
            migrationBuilder.DropIndex(
                name: "IX_AppUserEvents_EventId",
                table: "AppUserEvents");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEvents_EventId_AppUserId",
                table: "AppUserEvents",
                columns: new[] { "EventId", "AppUserId" },
                unique: true,
                filter: "\"AppUserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the index goes back. The merged rows stay merged; nothing paid depends on them
            // being split again.
            migrationBuilder.DropIndex(
                name: "IX_AppUserEvents_EventId_AppUserId",
                table: "AppUserEvents");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEvents_EventId",
                table: "AppUserEvents",
                column: "EventId");
        }
    }
}
