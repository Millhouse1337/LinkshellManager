using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddOutsidePartySignup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OutsidePartySignupEnabled",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DiscordUserId",
                table: "EventPartySlotSignups",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscordUserId",
                table: "AppUserEvents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventPartySlotSignups_EventId_DiscordUserId",
                table: "EventPartySlotSignups",
                columns: new[] { "EventId", "DiscordUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EventPartySlotSignups_EventId_DiscordUserId",
                table: "EventPartySlotSignups");

            migrationBuilder.DropColumn(
                name: "OutsidePartySignupEnabled",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "DiscordUserId",
                table: "EventPartySlotSignups");

            migrationBuilder.DropColumn(
                name: "DiscordUserId",
                table: "AppUserEvents");
        }
    }
}
