using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveClaimShieldCapturesAtEndCamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EventHistoryId",
                table: "ClaimShieldCaptures",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClaimShieldCaptures_EventHistoryId",
                table: "ClaimShieldCaptures",
                column: "EventHistoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_ClaimShieldCaptures_EventHistories_EventHistoryId",
                table: "ClaimShieldCaptures",
                column: "EventHistoryId",
                principalTable: "EventHistories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClaimShieldCaptures_EventHistories_EventHistoryId",
                table: "ClaimShieldCaptures");

            migrationBuilder.DropIndex(
                name: "IX_ClaimShieldCaptures_EventHistoryId",
                table: "ClaimShieldCaptures");

            migrationBuilder.DropColumn(
                name: "EventHistoryId",
                table: "ClaimShieldCaptures");
        }
    }
}
