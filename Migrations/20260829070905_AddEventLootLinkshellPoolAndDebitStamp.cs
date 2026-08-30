using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddEventLootLinkshellPoolAndDebitStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DkpDebitedAt",
                table: "EventLootDetails",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DkpPoolId",
                table: "EventLootDetails",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LinkshellId",
                table: "EventLootDetails",
                type: "integer",
                nullable: true);

            // Backfill BEFORE the foreign key goes on, or every pre-existing row would fail it.
            // Loot reached its linkshell through Event/EventHistory until now, so that is where
            // the value comes from. A row with neither (both FKs are SetNull, so both parents can
            // be gone) keeps a null linkshell rather than being deleted -- it is still a real DKP
            // debit somebody paid, and dropping it silently would be worse than an orphan.
            migrationBuilder.Sql(@"
                UPDATE ""EventLootDetails"" AS l
                SET ""LinkshellId"" = COALESCE(
                    (SELECT e.""LinkshellId"" FROM ""Events"" e WHERE e.""Id"" = l.""EventId""),
                    (SELECT h.""LinkshellId"" FROM ""EventHistories"" h WHERE h.""Id"" = l.""EventHistoryId"")
                )
                WHERE l.""LinkshellId"" IS NULL;
            ");

            migrationBuilder.CreateIndex(
                name: "IX_EventLootDetails_DkpPoolId",
                table: "EventLootDetails",
                column: "DkpPoolId");

            migrationBuilder.CreateIndex(
                name: "IX_EventLootDetails_LinkshellId",
                table: "EventLootDetails",
                column: "LinkshellId");

            migrationBuilder.AddForeignKey(
                name: "FK_EventLootDetails_DkpPools_DkpPoolId",
                table: "EventLootDetails",
                column: "DkpPoolId",
                principalTable: "DkpPools",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_EventLootDetails_Linkshells_LinkshellId",
                table: "EventLootDetails",
                column: "LinkshellId",
                principalTable: "Linkshells",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EventLootDetails_DkpPools_DkpPoolId",
                table: "EventLootDetails");

            migrationBuilder.DropForeignKey(
                name: "FK_EventLootDetails_Linkshells_LinkshellId",
                table: "EventLootDetails");

            migrationBuilder.DropIndex(
                name: "IX_EventLootDetails_DkpPoolId",
                table: "EventLootDetails");

            migrationBuilder.DropIndex(
                name: "IX_EventLootDetails_LinkshellId",
                table: "EventLootDetails");

            migrationBuilder.DropColumn(
                name: "DkpDebitedAt",
                table: "EventLootDetails");

            migrationBuilder.DropColumn(
                name: "DkpPoolId",
                table: "EventLootDetails");

            migrationBuilder.DropColumn(
                name: "LinkshellId",
                table: "EventLootDetails");
        }
    }
}
