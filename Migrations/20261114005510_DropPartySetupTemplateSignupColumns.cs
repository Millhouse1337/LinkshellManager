using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Drops the six old template sign-up columns on PartySetupSlots. Phase 5 stopped every read and
    // write of them (members sign up on event boards, EventPartySlotSignups), and that code is live.
    // They are all nullable, so Down simply adds them back, empty. The values are not restored: only
    // the pg_dump has them, and no code reads them.
    public partial class DropPartySetupTemplateSignupColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SignedUpAppUserId",
                table: "PartySetupSlots");

            migrationBuilder.DropColumn(
                name: "SignedUpAtUtc",
                table: "PartySetupSlots");

            migrationBuilder.DropColumn(
                name: "SignedUpCharacterName",
                table: "PartySetupSlots");

            migrationBuilder.DropColumn(
                name: "SignedUpMainJob",
                table: "PartySetupSlots");

            migrationBuilder.DropColumn(
                name: "SignedUpRole",
                table: "PartySetupSlots");

            migrationBuilder.DropColumn(
                name: "SignedUpSubJob",
                table: "PartySetupSlots");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignedUpAppUserId",
                table: "PartySetupSlots",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedUpAtUtc",
                table: "PartySetupSlots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedUpCharacterName",
                table: "PartySetupSlots",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedUpMainJob",
                table: "PartySetupSlots",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedUpRole",
                table: "PartySetupSlots",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedUpSubJob",
                table: "PartySetupSlots",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }
    }
}
