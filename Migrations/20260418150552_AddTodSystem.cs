using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTodSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    MonsterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DayNumber = table.Column<int>(type: "integer", nullable: true),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Claim = table.Column<bool>(type: "boolean", nullable: false),
                    Cooldown = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RepopTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Interval = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TimeStamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TotalClaims = table.Column<int>(type: "integer", nullable: true),
                    TotalTods = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tods_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TodLootDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TodId = table.Column<int>(type: "integer", nullable: true),
                    ItemName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ItemWinner = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    WinningDkpSpent = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodLootDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodLootDetails_Tods_TodId",
                        column: x => x.TodId,
                        principalTable: "Tods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TodLootDetails_TodId",
                table: "TodLootDetails",
                column: "TodId");

            migrationBuilder.CreateIndex(
                name: "IX_Tods_LinkshellId_MonsterName",
                table: "Tods",
                columns: new[] { "LinkshellId", "MonsterName" });

            migrationBuilder.CreateIndex(
                name: "IX_Tods_LinkshellId_Time",
                table: "Tods",
                columns: new[] { "LinkshellId", "Time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TodLootDetails");

            migrationBuilder.DropTable(
                name: "Tods");
        }
    }
}
