using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkshellMonsterTimings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LinkshellMonsterTimings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    MonsterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NormalizedMonsterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    WindowCount = table.Column<int>(type: "integer", nullable: true),
                    WindowCadenceMinutes = table.Column<int>(type: "integer", nullable: true),
                    CooldownMinutes = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    IsCustom = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellMonsterTimings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellMonsterTimings_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellMonsterTimings_LinkshellId_NormalizedMonsterName",
                table: "LinkshellMonsterTimings",
                columns: new[] { "LinkshellId", "NormalizedMonsterName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkshellMonsterTimings");
        }
    }
}
