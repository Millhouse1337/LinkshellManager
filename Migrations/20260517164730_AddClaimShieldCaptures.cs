using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddClaimShieldCaptures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClaimShieldCaptures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    MonsterName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Won = table.Column<bool>(type: "boolean", nullable: false),
                    TotalPlayers = table.Column<int>(type: "integer", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CapturedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CapturedMessage = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MemberCount = table.Column<int>(type: "integer", nullable: false),
                    MatchedCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimShieldCaptures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimShieldCaptures_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClaimShieldCaptureMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CaptureId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    Matched = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimShieldCaptureMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimShieldCaptureMembers_ClaimShieldCaptures_CaptureId",
                        column: x => x.CaptureId,
                        principalTable: "ClaimShieldCaptures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptureMembers_CaptureId",
                table: "ClaimShieldCaptureMembers",
                column: "CaptureId");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptures_LinkshellId_CapturedAtUtc",
                table: "ClaimShieldCaptures",
                columns: new[] { "LinkshellId", "CapturedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClaimShieldCaptureMembers");

            migrationBuilder.DropTable(
                name: "ClaimShieldCaptures");
        }
    }
}
