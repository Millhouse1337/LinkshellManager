using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddEventWindowRosterSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventWindowRosterSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    WindowNumber = table.Column<int>(type: "integer", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AllianceName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AllianceSortOrder = table.Column<int>(type: "integer", nullable: false),
                    PartyName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PartySortOrder = table.Column<int>(type: "integer", nullable: false),
                    SlotSortOrder = table.Column<int>(type: "integer", nullable: false),
                    SlotLabel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    IsPartyLeader = table.Column<bool>(type: "boolean", nullable: false),
                    IsAllianceLeader = table.Column<bool>(type: "boolean", nullable: false),
                    WasLocked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventWindowRosterSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventWindowRosterSnapshots_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventWindowRosterSnapshots_EventId_WindowNumber",
                table: "EventWindowRosterSnapshots",
                columns: new[] { "EventId", "WindowNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventWindowRosterSnapshots");
        }
    }
}
