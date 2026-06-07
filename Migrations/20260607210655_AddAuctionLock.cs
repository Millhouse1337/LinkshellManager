using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAuctionLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AuctionsLocked",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CanLockAuctions",
                table: "LinkshellRoles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Grant the new permission to existing system Leader/Officer roles so
            // current linkshells can lock auctions without re-seeding (mirrors
            // BackfillManageInvitesPermission).
            migrationBuilder.Sql(
                @"UPDATE ""LinkshellRoles"" SET ""CanLockAuctions"" = true
                  WHERE ""IsSystem"" = true AND ""Name"" IN ('Leader', 'Officer');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuctionsLocked",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "CanLockAuctions",
                table: "LinkshellRoles");
        }
    }
}
