using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddHnmAutoSnapshotAndTodKilled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppUserEventWindows_AppUserEvents_AppUserEventId",
                table: "AppUserEventWindows");

            migrationBuilder.DropIndex(
                name: "IX_AppUserEventWindows_EventAttendanceWindowId",
                table: "AppUserEventWindows");

            migrationBuilder.AddColumn<bool>(
                name: "Killed",
                table: "Tods",
                type: "boolean",
                nullable: true);

            // 20, not the scaffolder's 0. The column is clamped to [5, 300] on read, so a
            // backfilled 0 would silently become a 5-second capture delay on every existing
            // linkshell the moment one of them enables the feature.
            migrationBuilder.AddColumn<int>(
                name: "HnmAutoSnapshotDelaySeconds",
                table: "Linkshells",
                type: "integer",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<bool>(
                name: "HnmAutoSnapshotEnabled",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<int>(
                name: "AppUserEventId",
                table: "AppUserEventWindows",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "AppUserId",
                table: "AppUserEventWindows",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CharacterName",
                table: "AppUserEventWindows",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            // Backfill the denormalized identity from the join BEFORE anything reads it.
            // HnmStandardCampFinalizer now folds credit on AppUserId and filters out rows where
            // it's null, so without this every snapshot on a camp that is live across this deploy
            // would go invisible and pay nobody at End Camp.
            migrationBuilder.Sql(@"
                UPDATE ""AppUserEventWindows"" w
                SET ""AppUserId"" = e.""AppUserId"",
                    ""CharacterName"" = e.""CharacterName""
                FROM ""AppUserEvents"" e
                WHERE w.""AppUserEventId"" = e.""Id"";");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventWindows_EventAttendanceWindowId_AppUserId",
                table: "AppUserEventWindows",
                columns: new[] { "EventAttendanceWindowId", "AppUserId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AppUserEventWindows_AppUserEvents_AppUserEventId",
                table: "AppUserEventWindows",
                column: "AppUserEventId",
                principalTable: "AppUserEvents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppUserEventWindows_AppUserEvents_AppUserEventId",
                table: "AppUserEventWindows");

            migrationBuilder.DropIndex(
                name: "IX_AppUserEventWindows_EventAttendanceWindowId_AppUserId",
                table: "AppUserEventWindows");

            migrationBuilder.DropColumn(
                name: "Killed",
                table: "Tods");

            migrationBuilder.DropColumn(
                name: "HnmAutoSnapshotDelaySeconds",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "HnmAutoSnapshotEnabled",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "AppUserId",
                table: "AppUserEventWindows");

            migrationBuilder.DropColumn(
                name: "CharacterName",
                table: "AppUserEventWindows");

            migrationBuilder.AlterColumn<int>(
                name: "AppUserEventId",
                table: "AppUserEventWindows",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUserEventWindows_EventAttendanceWindowId",
                table: "AppUserEventWindows",
                column: "EventAttendanceWindowId");

            migrationBuilder.AddForeignKey(
                name: "FK_AppUserEventWindows_AppUserEvents_AppUserEventId",
                table: "AppUserEventWindows",
                column: "AppUserEventId",
                principalTable: "AppUserEvents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
