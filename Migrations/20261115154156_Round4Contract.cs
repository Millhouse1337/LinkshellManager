using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // The "contract" step of Round 4 (deploy 4, plan D10). Deploy 3 took every reader and writer
    // away from these, so this only drops what nothing uses any more (docs/round4-drop-manifest.md):
    // - AppUserLinkshells.JobLevels / StrongJobs / MeritJobs: old copies of the main's jobs. The jobs
    //   live on the account (AspNetUsers, and Characters) since AccountJobsAndKeyItemAccounts.
    // - ChartMemberKeyItems.MembershipId and its one-tick-per-membership unique index: ticks belong
    //   to an account (AppUserId) since RosterSwitch, and new ticks leave it NULL since deploy 3.
    // - IX_HnmRecurringBoards_LinkshellId_MonsterName: the SpawnKey unique index is stricter.
    // - IX_Tods_LinkshellId_MonsterName: ToDs are looked up by SpawnKey, or by linkshell and time.
    //
    // No row is deleted or changed, and no DKP, gil, ledger, journal or loot table is touched.
    // Safe to run twice (IF EXISTS).
    //
    // Down re-adds the columns EMPTY and re-creates the indexes (ReAddSql). A rollback to the deploy
    // 3 build, which still maps the columns, needs RollbackSql run by hand first. What the columns
    // held comes back only from the pre-deploy pg_dump.
    public partial class Round4Contract : Migration
    {
        public const string MigrationId = "20261115154156_Round4Contract";

        // What Down runs: the columns back EMPTY, and the indexes. All columns are nullable, so no
        // default is needed; the indexes cannot fail on the rows a deploy 4 build writes (the
        // MembershipId column comes back all NULL, and the SpawnKey unique index already allows
        // only one board per monster name).
        public const string ReAddSql = """
            ALTER TABLE "AppUserLinkshells"
                ADD COLUMN IF NOT EXISTS "JobLevels" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "StrongJobs" jsonb NULL,
                ADD COLUMN IF NOT EXISTS "MeritJobs" jsonb NULL;
            ALTER TABLE "ChartMemberKeyItems" ADD COLUMN IF NOT EXISTS "MembershipId" integer NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_Membershi~"
                ON "ChartMemberKeyItems" ("LinkshellId", "Board", "KeyItemName", "MembershipId");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_HnmRecurringBoards_LinkshellId_MonsterName"
                ON "HnmRecurringBoards" ("LinkshellId", "MonsterName");
            CREATE INDEX IF NOT EXISTS "IX_Tods_LinkshellId_MonsterName"
                ON "Tods" ("LinkshellId", "MonsterName");
            """;

        // The owner runs this by hand (after a backup) BEFORE swapping back to the deploy 3 build
        // (72cbe56): that build still maps these columns, so without them every query that loads a
        // membership or a key-item tick fails. It is ReAddSql plus forgetting this migration, so
        // the next deploy 4 start runs Up again and the drops happen then. Safe to run twice.
        // (Round4ContractRoundTripTests runs exactly this text.)
        public const string RollbackSql = ReAddSql + $"""

            DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '{MigrationId}';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_Tods_LinkshellId_MonsterName";
                DROP INDEX IF EXISTS "IX_HnmRecurringBoards_LinkshellId_MonsterName";
                DROP INDEX IF EXISTS "IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_Membershi~";
                ALTER TABLE "ChartMemberKeyItems" DROP COLUMN IF EXISTS "MembershipId";
                ALTER TABLE "AppUserLinkshells"
                    DROP COLUMN IF EXISTS "JobLevels",
                    DROP COLUMN IF EXISTS "MeritJobs",
                    DROP COLUMN IF EXISTS "StrongJobs";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ReAddSql);
        }
    }
}
