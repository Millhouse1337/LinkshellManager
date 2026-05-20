using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPartySetupSlotSignupJobFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
    }
}
