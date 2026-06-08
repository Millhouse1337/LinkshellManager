using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddJobStrengthFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int[]>(
                name: "Alt1StrongJobs",
                table: "AspNetUsers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int[]>(
                name: "Alt2StrongJobs",
                table: "AspNetUsers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int[]>(
                name: "StrongJobs",
                table: "AppUserLinkshells",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Alt1StrongJobs",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "Alt2StrongJobs",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StrongJobs",
                table: "AppUserLinkshells");
        }
    }
}
