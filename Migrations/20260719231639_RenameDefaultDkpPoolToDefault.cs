using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RenameDefaultDkpPoolToDefault : Migration
    {
        // Data-only: the seeded default group was called "Main"; it's now "Default".
        //
        // Deliberately narrow. Only a group that is BOTH the default AND still carries the exact
        // seed name is touched, so a group an officer named themselves is left alone. The NOT
        // EXISTS guard keeps the unique (LinkshellId, Name) index satisfied for any linkshell that
        // already has a group called "Default" — those keep "Main" rather than failing the deploy.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "DkpPools" AS p
                SET "Name" = 'Default'
                WHERE p."IsDefault" = TRUE
                  AND p."Name" = 'Main'
                  AND NOT EXISTS (
                      SELECT 1 FROM "DkpPools" AS other
                      WHERE other."LinkshellId" = p."LinkshellId"
                        AND other."Name" = 'Default'
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "DkpPools" AS p
                SET "Name" = 'Main'
                WHERE p."IsDefault" = TRUE
                  AND p."Name" = 'Default'
                  AND NOT EXISTS (
                      SELECT 1 FROM "DkpPools" AS other
                      WHERE other."LinkshellId" = p."LinkshellId"
                        AND other."Name" = 'Main'
                  );
                """);
        }
    }
}
