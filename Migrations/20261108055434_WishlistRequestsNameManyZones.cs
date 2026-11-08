using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// An item request can name several zones (an item that drops in more than one), so the single
    /// ChartWishlistRequests.Boss column becomes a ChartWishlistRequestZones table, one row per zone.
    /// Each existing request's zone is copied across BEFORE the column is dropped; a request with no
    /// zone ("anywhere") gets no rows.
    /// </summary>
    public partial class WishlistRequestsNameManyZones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChartWishlistRequestZones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RequestId = table.Column<int>(type: "integer", nullable: false),
                    Boss = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChartWishlistRequestZones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChartWishlistRequestZones_ChartWishlistRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "ChartWishlistRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChartWishlistRequestZones_RequestId_Boss",
                table: "ChartWishlistRequestZones",
                columns: new[] { "RequestId", "Boss" },
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO "ChartWishlistRequestZones" ("RequestId", "Boss")
                SELECT "Id", "Boss" FROM "ChartWishlistRequests"
                WHERE "Boss" IS NOT NULL AND btrim("Boss") <> '';
                """);

            migrationBuilder.DropColumn(
                name: "Boss",
                table: "ChartWishlistRequests");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Boss",
                table: "ChartWishlistRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // The old column holds ONE zone, so a request naming several keeps its first.
            migrationBuilder.Sql(
                """
                UPDATE "ChartWishlistRequests" r
                SET "Boss" = (
                    SELECT z."Boss" FROM "ChartWishlistRequestZones" z
                    WHERE z."RequestId" = r."Id"
                    ORDER BY z."Id"
                    LIMIT 1);
                """);

            migrationBuilder.DropTable(
                name: "ChartWishlistRequestZones");
        }
    }
}
