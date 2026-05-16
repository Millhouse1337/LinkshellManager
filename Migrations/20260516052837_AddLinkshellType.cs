using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkshellType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // defaultValue "Both" so existing linkshells keep seeing all
            // content (matches the C# model default and Normalize()'s
            // fail-open). New rows also default to Both via the model.
            migrationBuilder.AddColumn<string>(
                name: "LinkshellType",
                table: "Linkshells",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Both");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LinkshellType",
                table: "Linkshells");
        }
    }
}
