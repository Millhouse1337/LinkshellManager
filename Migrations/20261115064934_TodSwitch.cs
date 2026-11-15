using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // The "switch" step for ToD durations and spawn keys (round 4, D7). From this deploy the app
    // finds ToDs, pending ToDs and standing boards by SpawnKey (one equality instead of the list of
    // every spelling of a spawn), so the keys must be right before it starts.
    //
    // 1. The three derived columns are filled again from their text with the expand step's own SQL
    //    (AddTodDurationsAndSpawnKeys.FillSql), which only writes rows that differ and says how many.
    //    That repairs anything an older build saved meanwhile.
    //
    // 2. Standing boards the app can never use are DELETED, each id listed in the NOTICE:
    //    - a board with no spawn key (its MonsterName is blank): no lookup can find it and the
    //      poller does nothing with it;
    //    - a second board for one spawn in one linkshell. The one kept is the one in use: enabled
    //      first, then the most recently updated, then the newest id -- the same rule as
    //      MergeHnmRecurringBoardsPerSpawn. Nothing references HnmRecurringBoards.Id (no foreign
    //      key points at it, and no column stores it), so nothing else changes.
    //    Boards are re-post templates: they hold no DKP or gil. Production had none of either kind.
    //
    // 3. HnmRecurringBoards.SpawnKey becomes required, and its index becomes unique: one standing
    //    board per spawn per linkshell. The old unique (LinkshellId, MonsterName) index and the ToD
    //    name indexes stay for now.
    //
    // Safe to run twice: each step only touches rows that are still wrong, and says how many.
    //
    // Down makes the index plain and the column nullable again. The deleted boards are not brought
    // back (they are in the pre-deploy backup).
    public partial class TodSwitch : Migration
    {
        // Public so the Postgres round-trip test can run exactly this SQL again.
        public const string DeleteUnusableBoardsSql = """
            DO $$
            DECLARE blank_ids text; blanks integer; duplicate_ids text; duplicates integer;
            BEGIN
                SELECT string_agg("Id"::text, ', ' ORDER BY "Id"), count(*) INTO blank_ids, blanks
                FROM "HnmRecurringBoards" WHERE "SpawnKey" IS NULL;
                DELETE FROM "HnmRecurringBoards" WHERE "SpawnKey" IS NULL;

                SELECT string_agg(ranked."Id"::text, ', ' ORDER BY ranked."Id"), count(*)
                INTO duplicate_ids, duplicates
                FROM (
                    SELECT "Id", row_number() OVER (
                        PARTITION BY "LinkshellId", "SpawnKey"
                        ORDER BY "Enabled" DESC, "UpdatedAt" DESC, "Id" DESC) AS rk
                    FROM "HnmRecurringBoards") ranked
                WHERE ranked.rk > 1;
                DELETE FROM "HnmRecurringBoards" b USING (
                    SELECT "Id", row_number() OVER (
                        PARTITION BY "LinkshellId", "SpawnKey"
                        ORDER BY "Enabled" DESC, "UpdatedAt" DESC, "Id" DESC) AS rk
                    FROM "HnmRecurringBoards") ranked
                WHERE b."Id" = ranked."Id" AND ranked.rk > 1;

                RAISE NOTICE 'TodSwitch: deleted % standing board(s) with no monster name (ids: %) and % second board(s) for one spawn (ids: %).',
                    blanks, coalesce(blank_ids, 'none'), duplicates, coalesce(duplicate_ids, 'none');
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AddTodDurationsAndSpawnKeys.FillSql);
            migrationBuilder.Sql(DeleteUnusableBoardsSql);

            migrationBuilder.DropIndex(
                name: "IX_HnmRecurringBoards_LinkshellId_SpawnKey",
                table: "HnmRecurringBoards");

            migrationBuilder.AlterColumn<string>(
                name: "SpawnKey",
                table: "HnmRecurringBoards",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_HnmRecurringBoards_LinkshellId_SpawnKey",
                table: "HnmRecurringBoards",
                columns: new[] { "LinkshellId", "SpawnKey" },
                unique: true);
        }

        // Schema only: the index goes back to plain and the column to nullable. Deleted boards stay
        // deleted.
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HnmRecurringBoards_LinkshellId_SpawnKey",
                table: "HnmRecurringBoards");

            migrationBuilder.AlterColumn<string>(
                name: "SpawnKey",
                table: "HnmRecurringBoards",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.CreateIndex(
                name: "IX_HnmRecurringBoards_LinkshellId_SpawnKey",
                table: "HnmRecurringBoards",
                columns: new[] { "LinkshellId", "SpawnKey" });
        }
    }
}
