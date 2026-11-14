using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // JobRatings get real foreign keys to their linkshell, the rated account and the rater, all
    // ON DELETE CASCADE. Before this a rating could outlive the account or linkshell it belongs to,
    // and nothing ever cleaned it up.
    //
    // Ratings that already point at a missing linkshell or account are deleted first, or the
    // foreign keys could not be added. They are shown nowhere (every read joins the roster), and
    // they hold no DKP or gil. The pre-deploy check found none on production, so there this delete
    // removes nothing; it stays so the migration also works on a database that has some. It says
    // how many it removed in a NOTICE in the startup log.
    //
    // Safe to run twice: the delete finds nothing the second time.
    //
    // Down drops the three foreign keys and the two indexes. Deleted orphans are not brought back
    // (they are in the pre-deploy dump). The old build never reads these keys, so rolling back the
    // app without running Down also works.
    public partial class JobRatingsForeignKeys : Migration
    {
        // Public so the Postgres round-trip test can run exactly this SQL again.
        public const string DeleteOrphansSql = """
            DO $$
            DECLARE removed integer;
            BEGIN
                DELETE FROM "JobRatings" j
                WHERE NOT EXISTS (SELECT 1 FROM "Linkshells" l WHERE l."Id" = j."LinkshellId")
                   OR NOT EXISTS (SELECT 1 FROM "AspNetUsers" u WHERE u."Id" = j."TargetAppUserId")
                   OR NOT EXISTS (SELECT 1 FROM "AspNetUsers" u WHERE u."Id" = j."RaterAppUserId");
                GET DIAGNOSTICS removed = ROW_COUNT;
                RAISE NOTICE 'JobRatingsForeignKeys: deleted % orphan job rating(s).', removed;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DeleteOrphansSql);

            migrationBuilder.CreateIndex(
                name: "IX_JobRatings_RaterAppUserId",
                table: "JobRatings",
                column: "RaterAppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_JobRatings_TargetAppUserId",
                table: "JobRatings",
                column: "TargetAppUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_JobRatings_AspNetUsers_RaterAppUserId",
                table: "JobRatings",
                column: "RaterAppUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_JobRatings_AspNetUsers_TargetAppUserId",
                table: "JobRatings",
                column: "TargetAppUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_JobRatings_Linkshells_LinkshellId",
                table: "JobRatings",
                column: "LinkshellId",
                principalTable: "Linkshells",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobRatings_AspNetUsers_RaterAppUserId",
                table: "JobRatings");

            migrationBuilder.DropForeignKey(
                name: "FK_JobRatings_AspNetUsers_TargetAppUserId",
                table: "JobRatings");

            migrationBuilder.DropForeignKey(
                name: "FK_JobRatings_Linkshells_LinkshellId",
                table: "JobRatings");

            migrationBuilder.DropIndex(
                name: "IX_JobRatings_RaterAppUserId",
                table: "JobRatings");

            migrationBuilder.DropIndex(
                name: "IX_JobRatings_TargetAppUserId",
                table: "JobRatings");
        }
    }
}
