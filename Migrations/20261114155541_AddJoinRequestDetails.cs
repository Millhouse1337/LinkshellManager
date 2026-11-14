using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddJoinRequestDetails : Migration
    {
        // Before the one-open-request index: if a player has more than one open join request for
        // the same linkshell, keep the oldest open and mark the rest cancelled. Nothing is deleted.
        // Safe to run again: a second run finds no duplicates.
        public const string CancelDuplicateOpenJoinRequestsSql = """
            UPDATE "Invites" AS dup
            SET "Status" = 'JoinRequestCancelled',
                "ResolvedAtUtc" = now()
            WHERE dup."Status" = 'PendingJoinRequest'
              AND dup."AppUserId" IS NOT NULL
              AND EXISTS (
                  SELECT 1 FROM "Invites" AS keep
                  WHERE keep."Status" = 'PendingJoinRequest'
                    AND keep."LinkshellId" = dup."LinkshellId"
                    AND keep."AppUserId" = dup."AppUserId"
                    AND keep."Id" < dup."Id");
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAtUtc",
                table: "Invites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Message",
                table: "Invites",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAtUtc",
                table: "Invites",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedByAppUserId",
                table: "Invites",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.Sql(CancelDuplicateOpenJoinRequestsSql);

            migrationBuilder.CreateIndex(
                name: "IX_Invites_OneOpenJoinRequest",
                table: "Invites",
                columns: new[] { "LinkshellId", "AppUserId" },
                unique: true,
                filter: "\"Status\" = 'PendingJoinRequest'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invites_OneOpenJoinRequest",
                table: "Invites");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "Invites");

            migrationBuilder.DropColumn(
                name: "Message",
                table: "Invites");

            migrationBuilder.DropColumn(
                name: "ResolvedAtUtc",
                table: "Invites");

            migrationBuilder.DropColumn(
                name: "ResolvedByAppUserId",
                table: "Invites");
        }
    }
}
