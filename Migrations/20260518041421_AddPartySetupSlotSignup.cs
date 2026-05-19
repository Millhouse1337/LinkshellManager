using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPartySetupSlotSignup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
        }
    }
}
