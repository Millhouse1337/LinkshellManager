using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class OneMembershipPerLinkshell : Migration
    {
        // Merges every account's extra memberships in one linkshell into its OLDEST row, so the unique
        // index below can be created.
        //
        // Where the duplicates come from: every join path checks "already a member?" first, but a
        // double-clicked Discord Sign Up (or two join paths at once) both pass that check before either
        // saves. DKP then landed on whichever row a query picked, so the member's balance is split
        // across the rows.
        //
        // Money is kept, not removed: the oldest row gets the SUM of the rows' LinkshellDkp and seeded
        // lifetime totals. The ledger is keyed on the account (AppUserId), not on the membership row,
        // so it needs no change and still adds up to the merged balance. The two ledger watermarks
        // take the LATER of the rows' values, so no ledger row that either row had already folded into
        // its seed or left out of its pools is counted again. The kept row also takes the highest rank
        // (a Leader stays a Leader even if their leader row was the newer one), the earliest join date,
        // and any name / Discord id / job data it was missing. Everything else stays as the oldest row
        // had it.
        //
        // The chart tables point at a membership by its Id with no foreign key (see ChartPopItem), so
        // those columns are moved onto the kept row before the extra rows are deleted. A key item
        // ticked on both rows would break that table's one-tick-per-member index, so the extra tick is
        // dropped first (presence is the whole fact there, so nothing is lost).
        //
        // A no-op when there are no duplicates. Public so the Postgres tests run exactly this SQL
        // against seeded duplicates.

        // Every extra membership row (old_id) and the oldest row of its account in that linkshell
        // (keep_id). Repeated per statement; only the rows deleted at the very end change it.
        private const string DupCte = """
            dup AS (
                SELECT s."Id" AS old_id, s.keep_id
                FROM (
                    SELECT "Id", MIN("Id") OVER (PARTITION BY "LinkshellId", "AppUserId") AS keep_id
                    FROM "AppUserLinkshells"
                    WHERE "AppUserId" IS NOT NULL) s
                WHERE s."Id" <> s.keep_id)
            """;

        public const string MergeDuplicateMembershipsSql = $"""
            WITH grp AS (
                SELECT MIN("Id") AS keep_id,
                       SUM("LinkshellDkp") AS dkp,
                       SUM("SeededDkpEarned") AS seeded_earned,
                       SUM("SeededDkpSpent") AS seeded_spent,
                       MAX("DkpSeedLedgerId") AS seed_from,
                       MAX("DkpPoolLedgerFromId") AS pool_from,
                       MIN("DateJoined") AS joined,
                       (array_agg("Rank" ORDER BY
                            CASE lower(COALESCE("Rank", ''))
                                WHEN 'leader' THEN 0
                                WHEN 'officer' THEN 1
                                WHEN 'member' THEN 3
                                WHEN 'trial' THEN 4
                                WHEN '' THEN 5
                                ELSE 2
                            END, "Id"))[1] AS best_rank,
                       (array_agg("CharacterName" ORDER BY "Id") FILTER (WHERE "CharacterName" IS NOT NULL))[1] AS character_name,
                       (array_agg("DiscordUserId" ORDER BY "Id") FILTER (WHERE "DiscordUserId" IS NOT NULL))[1] AS discord_user_id,
                       (array_agg("JobLevels" ORDER BY "Id") FILTER (WHERE "JobLevels" IS NOT NULL))[1] AS job_levels,
                       (array_agg("StrongJobs" ORDER BY "Id") FILTER (WHERE "StrongJobs" IS NOT NULL))[1] AS strong_jobs,
                       (array_agg("MeritJobs" ORDER BY "Id") FILTER (WHERE "MeritJobs" IS NOT NULL))[1] AS merit_jobs
                FROM "AppUserLinkshells"
                WHERE "AppUserId" IS NOT NULL
                GROUP BY "LinkshellId", "AppUserId"
                HAVING COUNT(*) > 1)
            UPDATE "AppUserLinkshells" m
            SET "LinkshellDkp" = grp.dkp,
                "SeededDkpEarned" = grp.seeded_earned,
                "SeededDkpSpent" = grp.seeded_spent,
                "DkpSeedLedgerId" = grp.seed_from,
                "DkpPoolLedgerFromId" = grp.pool_from,
                "DateJoined" = grp.joined,
                "Rank" = grp.best_rank,
                "CharacterName" = grp.character_name,
                "DiscordUserId" = grp.discord_user_id,
                "JobLevels" = grp.job_levels,
                "StrongJobs" = grp.strong_jobs,
                "MeritJobs" = grp.merit_jobs
            FROM grp
            WHERE m."Id" = grp.keep_id;

            WITH {DupCte},
            ranked AS (
                SELECT k."Id",
                       ROW_NUMBER() OVER (
                           PARTITION BY k."LinkshellId", k."Board", k."KeyItemName", COALESCE(dup.keep_id, k."MembershipId")
                           ORDER BY (dup.old_id IS NULL) DESC, k."Id") AS rn
                FROM "ChartMemberKeyItems" k
                LEFT JOIN dup ON dup.old_id = k."MembershipId")
            DELETE FROM "ChartMemberKeyItems" k
            USING ranked
            WHERE k."Id" = ranked."Id" AND ranked.rn > 1;

            WITH {DupCte}
            UPDATE "ChartMemberKeyItems" k SET "MembershipId" = dup.keep_id
            FROM dup WHERE k."MembershipId" = dup.old_id;

            WITH {DupCte}
            UPDATE "ChartPopItemCredits" c SET "MembershipId" = dup.keep_id
            FROM dup WHERE c."MembershipId" = dup.old_id;

            WITH {DupCte}
            UPDATE "ChartPopItems" p SET "HeldByMembershipId" = dup.keep_id
            FROM dup WHERE p."HeldByMembershipId" = dup.old_id;

            WITH {DupCte}
            UPDATE "ChartWishlistRequests" r SET "RequestedByMembershipId" = dup.keep_id
            FROM dup WHERE r."RequestedByMembershipId" = dup.old_id;

            WITH {DupCte}
            DELETE FROM "AppUserLinkshells" m
            USING dup
            WHERE m."Id" = dup.old_id;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Merge first: the index cannot be created while any duplicate is left.
            migrationBuilder.Sql(MergeDuplicateMembershipsSql);

            // The new index starts with LinkshellId, so it also serves every "members of this
            // linkshell" read the old single-column index did.
            migrationBuilder.DropIndex(
                name: "IX_AppUserLinkshells_LinkshellId",
                table: "AppUserLinkshells");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserLinkshells_LinkshellId_AppUserId",
                table: "AppUserLinkshells",
                columns: new[] { "LinkshellId", "AppUserId" },
                unique: true,
                filter: "\"AppUserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the index goes back. The merged rows stay merged: the kept row holds the summed
            // balance, so no member's DKP depends on splitting them again.
            migrationBuilder.DropIndex(
                name: "IX_AppUserLinkshells_LinkshellId_AppUserId",
                table: "AppUserLinkshells");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserLinkshells_LinkshellId",
                table: "AppUserLinkshells",
                column: "LinkshellId");
        }
    }
}
