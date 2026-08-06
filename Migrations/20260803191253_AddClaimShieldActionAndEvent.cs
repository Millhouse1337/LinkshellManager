using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddClaimShieldActionAndEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EventId",
                table: "ClaimShieldCaptures",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActionMessage",
                table: "ClaimShieldCaptureMembers",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptures_EventId",
                table: "ClaimShieldCaptures",
                column: "EventId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClaimShieldCaptures_Events_EventId",
                table: "ClaimShieldCaptures",
                column: "EventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClaimShieldCaptures_Events_EventId",
                table: "ClaimShieldCaptures");

            migrationBuilder.DropIndex(
                name: "IX_ClaimShieldCaptures_EventId",
                table: "ClaimShieldCaptures");

            migrationBuilder.DropColumn(
                name: "EventId",
                table: "ClaimShieldCaptures");

            migrationBuilder.DropColumn(
                name: "ActionMessage",
                table: "ClaimShieldCaptureMembers");
        }
    }
}
