using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddJobRatingCharacterSlot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobRatings_LinkshellId_TargetAppUserId_RaterAppUserId_JobIn~",
                table: "JobRatings");

            migrationBuilder.AddColumn<int>(
                name: "CharacterSlot",
                table: "JobRatings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_JobRatings_LinkshellId_TargetAppUserId_RaterAppUserId_Chara~",
                table: "JobRatings",
                columns: new[] { "LinkshellId", "TargetAppUserId", "RaterAppUserId", "CharacterSlot", "JobIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobRatings_LinkshellId_TargetAppUserId_RaterAppUserId_Chara~",
                table: "JobRatings");

            migrationBuilder.DropColumn(
                name: "CharacterSlot",
                table: "JobRatings");

            migrationBuilder.CreateIndex(
                name: "IX_JobRatings_LinkshellId_TargetAppUserId_RaterAppUserId_JobIn~",
                table: "JobRatings",
                columns: new[] { "LinkshellId", "TargetAppUserId", "RaterAppUserId", "JobIndex" },
                unique: true);
        }
    }
}
