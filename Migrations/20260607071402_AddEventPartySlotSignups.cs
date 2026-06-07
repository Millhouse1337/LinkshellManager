using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddEventPartySlotSignups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventPartySlotSignups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    PartySetupSlotId = table.Column<int>(type: "integer", nullable: false),
                    AppUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CharacterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MainJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SubJob = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    SignedUpAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventPartySlotSignups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventPartySlotSignups_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EventPartySlotSignups_PartySetupSlots_PartySetupSlotId",
                        column: x => x.PartySetupSlotId,
                        principalTable: "PartySetupSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventPartySlotSignups_EventId_AppUserId",
                table: "EventPartySlotSignups",
                columns: new[] { "EventId", "AppUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_EventPartySlotSignups_EventId_PartySetupSlotId",
                table: "EventPartySlotSignups",
                columns: new[] { "EventId", "PartySetupSlotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventPartySlotSignups_PartySetupSlotId",
                table: "EventPartySlotSignups",
                column: "PartySetupSlotId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventPartySlotSignups");
        }
    }
}
