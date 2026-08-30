using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddSignupReadinessTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnfeebReady",
                table: "EventPartySlotSignups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RelicWeapon",
                table: "EventPartySlotSignups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ResistReady",
                table: "EventPartySlotSignups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnfeebReady",
                table: "AppUserEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RelicWeapon",
                table: "AppUserEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ResistReady",
                table: "AppUserEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnfeebReady",
                table: "EventPartySlotSignups");

            migrationBuilder.DropColumn(
                name: "RelicWeapon",
                table: "EventPartySlotSignups");

            migrationBuilder.DropColumn(
                name: "ResistReady",
                table: "EventPartySlotSignups");

            migrationBuilder.DropColumn(
                name: "EnfeebReady",
                table: "AppUserEvents");

            migrationBuilder.DropColumn(
                name: "RelicWeapon",
                table: "AppUserEvents");

            migrationBuilder.DropColumn(
                name: "ResistReady",
                table: "AppUserEvents");
        }
    }
}
