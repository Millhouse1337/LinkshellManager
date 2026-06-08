using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddBidPermissionAndTrialRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default true so EVERY existing role (Leader/Officer/Member + any custom
            // role) keeps the ability to bid, and new roles default to allowing it.
            // The built-in Trial role explicitly sets it false below.
            migrationBuilder.AddColumn<bool>(
                name: "CanBid",
                table: "LinkshellRoles",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Seed the built-in "Trial" role (no bidding) for every existing
            // linkshell that doesn't already have one. New linkshells get it from
            // LinkshellRoleDefaults; this backfills the rest immediately so it's
            // assignable right away on the web and in the Activity.
            migrationBuilder.Sql(@"
INSERT INTO ""LinkshellRoles"" (
    ""LinkshellId"", ""Name"", ""IsSystem"", ""SortOrder"",
    ""CanManageRoles"", ""CanManageMembers"", ""CanManageEvents"", ""CanModerateLiveEvent"",
    ""CanAddLoot"", ""CanManageInventory"", ""CanManageTreasury"", ""CanManageRules"",
    ""CanManageAnnouncements"", ""CanManageTods"", ""CanAuditDkp"", ""CanManageAuctions"",
    ""CanLockAuctions"", ""CanCustomizeLinkshell"", ""CanSubmitTodForApproval"",
    ""CanSubmitAttendanceForApproval"", ""CanManageParties"", ""CanManageInvites"", ""CanBid"")
SELECT l.""Id"", 'Trial', TRUE, 3,
    FALSE, FALSE, FALSE, FALSE,
    FALSE, FALSE, FALSE, FALSE,
    FALSE, FALSE, FALSE, FALSE,
    FALSE, FALSE, FALSE,
    FALSE, FALSE, FALSE, FALSE
FROM ""Linkshells"" l
WHERE NOT EXISTS (
    SELECT 1 FROM ""LinkshellRoles"" r
    WHERE r.""LinkshellId"" = l.""Id"" AND r.""Name"" = 'Trial');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DELETE FROM ""LinkshellRoles"" WHERE ""Name"" = 'Trial' AND ""IsSystem"" = TRUE;");

            migrationBuilder.DropColumn(
                name: "CanBid",
                table: "LinkshellRoles");
        }
    }
}
