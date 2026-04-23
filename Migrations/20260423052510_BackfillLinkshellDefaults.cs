using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class BackfillLinkshellDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""Linkshells""
                SET ""LootStructure"" = 'Dkp'
                WHERE ""LootStructure"" IS NULL OR ""LootStructure"" = '';
            ");

            migrationBuilder.Sql(@"
                UPDATE ""Linkshells""
                SET
                    ""EnableHnmSection"" = TRUE,
                    ""EnableMissions"" = TRUE,
                    ""EnableAuctions"" = TRUE,
                    ""EnableToDs"" = TRUE,
                    ""EnableEndgame"" = TRUE;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Backfill is idempotent; no rollback required.
        }
    }
}
