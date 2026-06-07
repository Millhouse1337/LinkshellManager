using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddDkpTemplateSeedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DkpTemplateTabName",
                table: "Linkshells",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DkpSeedLedgerId",
                table: "AppUserLinkshells",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "SeededDkpEarned",
                table: "AppUserLinkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "SeededDkpSpent",
                table: "AppUserLinkshells",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DkpTemplateTabName",
                table: "Linkshells");

            migrationBuilder.DropColumn(
                name: "DkpSeedLedgerId",
                table: "AppUserLinkshells");

            migrationBuilder.DropColumn(
                name: "SeededDkpEarned",
                table: "AppUserLinkshells");

            migrationBuilder.DropColumn(
                name: "SeededDkpSpent",
                table: "AppUserLinkshells");
        }
    }
}
