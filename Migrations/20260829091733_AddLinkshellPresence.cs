using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkshellPresence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LinkshellPresences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    MainCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ZoneId = table.Column<int>(type: "integer", nullable: true),
                    AllianceNumber = table.Column<int>(type: "integer", nullable: false),
                    AllianceKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsAllianceLeader = table.Column<bool>(type: "boolean", nullable: false),
                    MainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    MainJobLevel = table.Column<int>(type: "integer", nullable: true),
                    SubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SubJobLevel = table.Column<int>(type: "integer", nullable: true),
                    ReportedByCharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkshellPresences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkshellPresences_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellPresences_LinkshellId_CharacterName",
                table: "LinkshellPresences",
                columns: new[] { "LinkshellId", "CharacterName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LinkshellPresences_LinkshellId_LastSeenUtc",
                table: "LinkshellPresences",
                columns: new[] { "LinkshellId", "LastSeenUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkshellPresences");
        }
    }
}
