using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTodBoardWebhookFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PostTodBoard",
                table: "LinkshellDiscordWebhooks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TodBoardMessageId",
                table: "LinkshellDiscordWebhooks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PostTodBoard",
                table: "LinkshellDiscordWebhooks");

            migrationBuilder.DropColumn(
                name: "TodBoardMessageId",
                table: "LinkshellDiscordWebhooks");
        }
    }
}
