using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// Turns the Sky-only tables into board-generic Charts tables so Sea (and later Dynamis and
    /// Limbus) reuse them instead of getting a copy each: SkyPopItems → ChartPopItems with a Board
    /// column, God → Boss. Adds ChartBossProgresses for the officers' per-boss progress note.
    ///
    /// Written as RENAMEs rather than the drop-and-recreate EF generates, because Sky shipped one
    /// release earlier and a linkshell that already typed its pop items in would otherwise lose
    /// them. Existing rows backfill to Board = 'Sky', which is what they are.
    ///
    /// The constraint and index renames matter as much as the table ones: Postgres keeps the old
    /// names through a table rename, so without these a later migration that drops
    /// "PK_ChartPopItems" would fail against an object still called "PK_SkyPopItems".
    /// </summary>
    public partial class GeneralizeChartsForSea : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "SkyPopItems" RENAME TO "ChartPopItems";
                ALTER TABLE "SkyPopItemCredits" RENAME TO "ChartPopItemCredits";

                -- "Jailer of Temperance" does not fit the 32 chars a Sky god needed.
                ALTER TABLE "ChartPopItems" RENAME COLUMN "God" TO "Boss";
                ALTER TABLE "ChartPopItems" ALTER COLUMN "Boss" TYPE character varying(64);

                -- Every existing row is a Sky row; the default exists only to backfill them, so it
                -- is dropped immediately and the board is always written explicitly from here on.
                ALTER TABLE "ChartPopItems"
                    ADD COLUMN "Board" character varying(16) NOT NULL DEFAULT 'Sky';
                ALTER TABLE "ChartPopItems" ALTER COLUMN "Board" DROP DEFAULT;

                ALTER TABLE "ChartPopItemCredits" RENAME COLUMN "SkyPopItemId" TO "ChartPopItemId";

                ALTER TABLE "ChartPopItems"
                    RENAME CONSTRAINT "PK_SkyPopItems" TO "PK_ChartPopItems";
                ALTER TABLE "ChartPopItems"
                    RENAME CONSTRAINT "FK_SkyPopItems_Linkshells_LinkshellId"
                    TO "FK_ChartPopItems_Linkshells_LinkshellId";
                ALTER TABLE "ChartPopItems"
                    RENAME CONSTRAINT "CK_SkyPopItems_Quantity_NonNegative"
                    TO "CK_ChartPopItems_Quantity_NonNegative";

                ALTER TABLE "ChartPopItemCredits"
                    RENAME CONSTRAINT "PK_SkyPopItemCredits" TO "PK_ChartPopItemCredits";
                ALTER TABLE "ChartPopItemCredits"
                    RENAME CONSTRAINT "FK_SkyPopItemCredits_SkyPopItems_SkyPopItemId"
                    TO "FK_ChartPopItemCredits_ChartPopItems_ChartPopItemId";

                ALTER INDEX "IX_SkyPopItemCredits_LinkshellId_MembershipId"
                    RENAME TO "IX_ChartPopItemCredits_LinkshellId_MembershipId";
                ALTER INDEX "IX_SkyPopItemCredits_SkyPopItemId"
                    RENAME TO "IX_ChartPopItemCredits_ChartPopItemId";

                -- Recreated rather than renamed: Board joins the key, so the column list changes.
                DROP INDEX "IX_SkyPopItems_LinkshellId_God_SortOrder";
                CREATE INDEX "IX_ChartPopItems_LinkshellId_Board_Boss_SortOrder"
                    ON "ChartPopItems" ("LinkshellId", "Board", "Boss", "SortOrder");
                """);

            migrationBuilder.CreateTable(
                name: "ChartBossProgresses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    Board = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Boss = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProgressNote = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedByAppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UpdatedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChartBossProgresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChartBossProgresses_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChartBossProgresses_LinkshellId_Board_Boss",
                table: "ChartBossProgresses",
                columns: new[] { "LinkshellId", "Board", "Boss" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ChartBossProgresses");

            // Sea rows have no home in the Sky-only shape, so they go rather than being silently
            // relabelled as Sky pop items. Reverting this migration is reverting the Sea feature.
            migrationBuilder.Sql("""
                DELETE FROM "ChartPopItems" WHERE "Board" <> 'Sky';

                DROP INDEX "IX_ChartPopItems_LinkshellId_Board_Boss_SortOrder";
                ALTER TABLE "ChartPopItems" DROP COLUMN "Board";

                ALTER INDEX "IX_ChartPopItemCredits_LinkshellId_MembershipId"
                    RENAME TO "IX_SkyPopItemCredits_LinkshellId_MembershipId";
                ALTER INDEX "IX_ChartPopItemCredits_ChartPopItemId"
                    RENAME TO "IX_SkyPopItemCredits_SkyPopItemId";

                ALTER TABLE "ChartPopItemCredits"
                    RENAME CONSTRAINT "FK_ChartPopItemCredits_ChartPopItems_ChartPopItemId"
                    TO "FK_SkyPopItemCredits_SkyPopItems_SkyPopItemId";
                ALTER TABLE "ChartPopItemCredits"
                    RENAME CONSTRAINT "PK_ChartPopItemCredits" TO "PK_SkyPopItemCredits";

                ALTER TABLE "ChartPopItems"
                    RENAME CONSTRAINT "CK_ChartPopItems_Quantity_NonNegative"
                    TO "CK_SkyPopItems_Quantity_NonNegative";
                ALTER TABLE "ChartPopItems"
                    RENAME CONSTRAINT "FK_ChartPopItems_Linkshells_LinkshellId"
                    TO "FK_SkyPopItems_Linkshells_LinkshellId";
                ALTER TABLE "ChartPopItems"
                    RENAME CONSTRAINT "PK_ChartPopItems" TO "PK_SkyPopItems";

                ALTER TABLE "ChartPopItemCredits" RENAME COLUMN "ChartPopItemId" TO "SkyPopItemId";
                ALTER TABLE "ChartPopItems" ALTER COLUMN "Boss" TYPE character varying(32);
                ALTER TABLE "ChartPopItems" RENAME COLUMN "Boss" TO "God";

                ALTER TABLE "ChartPopItemCredits" RENAME TO "SkyPopItemCredits";
                ALTER TABLE "ChartPopItems" RENAME TO "SkyPopItems";

                CREATE INDEX "IX_SkyPopItems_LinkshellId_God_SortOrder"
                    ON "SkyPopItems" ("LinkshellId", "God", "SortOrder");
                """);
        }
    }
}
