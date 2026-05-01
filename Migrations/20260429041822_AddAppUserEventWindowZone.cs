using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAppUserEventWindowZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent so it's safe to run alongside Program.cs's startup bootstrap.
            migrationBuilder.Sql(@"
                ALTER TABLE ""AppUserEventWindows"" ADD COLUMN IF NOT EXISTS ""Zone"" character varying(64) NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Zone",
                table: "AppUserEventWindows");
        }
    }
}
