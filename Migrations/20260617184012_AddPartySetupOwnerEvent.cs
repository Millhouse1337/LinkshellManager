using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddPartySetupOwnerEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OwnerEventId",
                table: "PartySetups",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartySetups_OwnerEventId",
                table: "PartySetups",
                column: "OwnerEventId",
                unique: true,
                filter: "\"OwnerEventId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_PartySetups_Events_OwnerEventId",
                table: "PartySetups",
                column: "OwnerEventId",
                principalTable: "Events",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PartySetups_Events_OwnerEventId",
                table: "PartySetups");

            migrationBuilder.DropIndex(
                name: "IX_PartySetups_OwnerEventId",
                table: "PartySetups");

            migrationBuilder.DropColumn(
                name: "OwnerEventId",
                table: "PartySetups");
        }
    }
}
