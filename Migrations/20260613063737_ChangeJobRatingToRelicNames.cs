using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class ChangeJobRatingToRelicNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RelicName",
                table: "JobRatings");

            migrationBuilder.AddColumn<string[]>(
                name: "RelicNames",
                table: "JobRatings",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RelicNames",
                table: "JobRatings");

            migrationBuilder.AddColumn<string>(
                name: "RelicName",
                table: "JobRatings",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }
    }
}
