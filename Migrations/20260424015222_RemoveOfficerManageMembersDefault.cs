using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOfficerManageMembersDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""LinkshellRoles""
                SET ""CanManageMembers"" = FALSE
                WHERE ""Name"" = 'Officer' AND ""IsSystem"" = TRUE;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""LinkshellRoles""
                SET ""CanManageMembers"" = TRUE
                WHERE ""Name"" = 'Officer' AND ""IsSystem"" = TRUE;
            ");
        }
    }
}
