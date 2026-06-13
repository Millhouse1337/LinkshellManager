using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCraftLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int[]>(
                name: "Alt1CraftLevels",
                table: "AspNetUsers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int[]>(
                name: "Alt2CraftLevels",
                table: "AspNetUsers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int[]>(
                name: "CraftLevels",
                table: "AspNetUsers",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Alt1CraftLevels",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Alt2CraftLevels",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "CraftLevels",
                table: "AspNetUsers");
        }
    }
}
