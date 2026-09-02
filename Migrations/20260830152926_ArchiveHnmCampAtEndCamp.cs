using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveHnmCampAtEndCamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CampEventHistoryId",
                table: "WindowEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WindowEvents_CampEventHistoryId",
                table: "WindowEvents",
                column: "CampEventHistoryId");

            migrationBuilder.AddForeignKey(
                name: "FK_WindowEvents_EventHistories_CampEventHistoryId",
                table: "WindowEvents",
                column: "CampEventHistoryId",
                principalTable: "EventHistories",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WindowEvents_EventHistories_CampEventHistoryId",
                table: "WindowEvents");

            migrationBuilder.DropIndex(
                name: "IX_WindowEvents_CampEventHistoryId",
                table: "WindowEvents");

            migrationBuilder.DropColumn(
                name: "CampEventHistoryId",
                table: "WindowEvents");
        }
    }
}
