using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMonsterClaimShieldToggle : Migration
    {
        /// <inheritdoc />
        // defaultValue TRUE, not EF's generated false. This is a backfill onto live rows: shipping
        // it as false would switch Claim Shield off for every monster of every linkshell the moment
        // it deployed, and the symptom -- captures silently stop -- is exactly the class of failure
        // this whole feature area has been plagued by. The column exists so an officer can turn a
        // monster OFF deliberately; until they do, nothing changes.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClaimShieldEnabled",
                table: "LinkshellMonsterTimings",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimShieldEnabled",
                table: "LinkshellMonsterTimings");
        }
    }
}
