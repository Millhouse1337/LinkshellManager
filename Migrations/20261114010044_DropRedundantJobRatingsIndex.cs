using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // (LinkshellId, TargetAppUserId) is the start of the unique rating index, which already answers
    // that lookup, so this second index only cost writes. Dropping an index never touches data;
    // Down puts it back.
    public partial class DropRedundantJobRatingsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobRatings_LinkshellId_TargetAppUserId",
                table: "JobRatings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_JobRatings_LinkshellId_TargetAppUserId",
                table: "JobRatings",
                columns: new[] { "LinkshellId", "TargetAppUserId" });
        }
    }
}
