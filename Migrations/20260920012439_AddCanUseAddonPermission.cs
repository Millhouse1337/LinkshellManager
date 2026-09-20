using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCanUseAddonPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanUseAddon",
                table: "LinkshellRoles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // EVERY EXISTING ROLE GETS IT. The addon used to be gated on rank and, in the
            // Activity, on "Customize linkshell settings"; a permission that defaulted to off
            // would take the addon away from every linkshell already using it, on the very
            // deploy that introduced the setting. New linkshells get it from
            // LinkshellRoleDefaults, which grants it to all four default roles.
            migrationBuilder.Sql(@"UPDATE ""LinkshellRoles"" SET ""CanUseAddon"" = TRUE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanUseAddon",
                table: "LinkshellRoles");
        }
    }
}
