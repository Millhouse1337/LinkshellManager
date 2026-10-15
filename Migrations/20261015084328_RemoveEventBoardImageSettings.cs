using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RemoveEventBoardImageSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventBoardTheme",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "UseComponentsV2Boards",
                table: "Linkshells");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EventBoardTheme",
                table: "Linkshells",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Crystal");

            migrationBuilder.AddColumn<bool>(
                name: "UseComponentsV2Boards",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
