using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddHnmSignupBoards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedMonsterName",
                table: "Events",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HnmRecurringBoards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkshellId = table.Column<int>(type: "integer", nullable: false),
                    MonsterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LeadHours = table.Column<int>(type: "integer", nullable: false),
                    PartySetupId = table.Column<int>(type: "integer", nullable: true),
                    Details = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    EventLocation = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EventNameTemplate = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LastSourceTodId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByAppUserId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HnmRecurringBoards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HnmRecurringBoards_Linkshells_LinkshellId",
                        column: x => x.LinkshellId,
                        principalTable: "Linkshells",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HnmRecurringBoards_PartySetups_PartySetupId",
                        column: x => x.PartySetupId,
                        principalTable: "PartySetups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HnmRecurringBoards_LinkshellId_MonsterName",
                table: "HnmRecurringBoards",
                columns: new[] { "LinkshellId", "MonsterName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HnmRecurringBoards_PartySetupId",
                table: "HnmRecurringBoards",
                column: "PartySetupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HnmRecurringBoards");

            migrationBuilder.DropColumn(
                name: "AssignedMonsterName",
                table: "Events");
        }
    }
}
