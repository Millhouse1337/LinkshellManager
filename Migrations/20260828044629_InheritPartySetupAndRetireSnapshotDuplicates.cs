using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class InheritPartySetupAndRetireSnapshotDuplicates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Retire the duplicate statuses by HEALING the rows that carry them, before the column
            // they leaned on goes away.
            //
            // This is not cosmetic. A snapshot flagged PossibleDuplicate/Duplicate was excluded
            // from the combined roster (BuildCombinedMembers filters to Active), so anyone who
            // appeared ONLY in a flagged capture has been silently uncredited for as long as the
            // flag stood. Restoring them to Active puts those people back in the roster their
            // Window Event will pay -- which is the behaviour the alliance-aware merge makes
            // correct: two captures of one alliance union by character name, so a name appearing
            // in both is counted once, not twice.
            //
            // Deliberately NOT touching Ignored: that is an officer's explicit "don't count this",
            // and it stays a supported status.
            migrationBuilder.Sql(
                """
                UPDATE "AttendanceSnapshots"
                SET "SnapshotStatus" = 'Active'
                WHERE "SnapshotStatus" IN ('PossibleDuplicate', 'Duplicate');
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_AttendanceSnapshots_AttendanceSnapshots_DuplicateOfSnapshot~",
                table: "AttendanceSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_EventHistories_LinkshellId",
                table: "EventHistories");

            migrationBuilder.DropIndex(
                name: "IX_AttendanceSnapshots_DuplicateOfSnapshotId",
                table: "AttendanceSnapshots");

            migrationBuilder.DropColumn(
                name: "DuplicateOfSnapshotId",
                table: "AttendanceSnapshots");

            migrationBuilder.AddColumn<int>(
                name: "ClonedFromPartySetupId",
                table: "PartySetups",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PartySetupId",
                table: "EventHistories",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartySetups_ClonedFromPartySetupId",
                table: "PartySetups",
                column: "ClonedFromPartySetupId");

            migrationBuilder.CreateIndex(
                name: "IX_EventHistories_LinkshellId_EventName",
                table: "EventHistories",
                columns: new[] { "LinkshellId", "EventName" });

            migrationBuilder.CreateIndex(
                name: "IX_EventHistories_PartySetupId",
                table: "EventHistories",
                column: "PartySetupId");

            migrationBuilder.AddForeignKey(
                name: "FK_EventHistories_PartySetups_PartySetupId",
                table: "EventHistories",
                column: "PartySetupId",
                principalTable: "PartySetups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_PartySetups_PartySetups_ClonedFromPartySetupId",
                table: "PartySetups",
                column: "ClonedFromPartySetupId",
                principalTable: "PartySetups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EventHistories_PartySetups_PartySetupId",
                table: "EventHistories");

            migrationBuilder.DropForeignKey(
                name: "FK_PartySetups_PartySetups_ClonedFromPartySetupId",
                table: "PartySetups");

            migrationBuilder.DropIndex(
                name: "IX_PartySetups_ClonedFromPartySetupId",
                table: "PartySetups");

            migrationBuilder.DropIndex(
                name: "IX_EventHistories_LinkshellId_EventName",
                table: "EventHistories");

            migrationBuilder.DropIndex(
                name: "IX_EventHistories_PartySetupId",
                table: "EventHistories");

            migrationBuilder.DropColumn(
                name: "ClonedFromPartySetupId",
                table: "PartySetups");

            migrationBuilder.DropColumn(
                name: "PartySetupId",
                table: "EventHistories");

            migrationBuilder.AddColumn<int>(
                name: "DuplicateOfSnapshotId",
                table: "AttendanceSnapshots",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventHistories_LinkshellId",
                table: "EventHistories",
                column: "LinkshellId");

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceSnapshots_DuplicateOfSnapshotId",
                table: "AttendanceSnapshots",
                column: "DuplicateOfSnapshotId");

            migrationBuilder.AddForeignKey(
                name: "FK_AttendanceSnapshots_AttendanceSnapshots_DuplicateOfSnapshot~",
                table: "AttendanceSnapshots",
                column: "DuplicateOfSnapshotId",
                principalTable: "AttendanceSnapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
