using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMeritJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "Alt1MeritJobs",
                table: "AspNetUsers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "Alt2MeritJobs",
                table: "AspNetUsers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "MeritJobs",
                table: "AppUserLinkshells",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Alt1MeritJobs",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Alt2MeritJobs",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "MeritJobs",
                table: "AppUserLinkshells");
        }
    }
}
