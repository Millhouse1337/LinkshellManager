using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RecordEventHistoryPopWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PopWindow",
                table: "EventHistories",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PopWindowCount",
                table: "EventHistories",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PopWindow",
                table: "EventHistories");

            migrationBuilder.DropColumn(
                name: "PopWindowCount",
                table: "EventHistories");
        }
    }
}
