using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkshellEventBoardTheme : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EventBoardTheme",
                table: "Linkshells",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Crystal");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventBoardTheme",
                table: "Linkshells");
        }
    }
}
