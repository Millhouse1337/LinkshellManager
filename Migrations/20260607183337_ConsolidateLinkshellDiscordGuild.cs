using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateLinkshellDiscordGuild : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve any lock set only via the Activity's "Lock to this server"
            // button (which wrote LockedToDiscordGuildId, not DiscordGuildId) by
            // copying it onto the surviving column before the redundant one is
            // dropped. The matching name is carried by the RenameColumn below.
            migrationBuilder.Sql(
                "UPDATE \"Linkshells\" SET \"DiscordGuildId\" = \"LockedToDiscordGuildId\" " +
                "WHERE (\"DiscordGuildId\" IS NULL OR \"DiscordGuildId\" = '') " +
                "AND \"LockedToDiscordGuildId\" IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "LockedToDiscordGuildId",
                table: "Linkshells");

            migrationBuilder.RenameColumn(
                name: "LockedToDiscordGuildName",
                table: "Linkshells",
                newName: "DiscordGuildName");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "DiscordGuildName",
                table: "Linkshells",
                newName: "LockedToDiscordGuildName");

            migrationBuilder.AddColumn<string>(
                name: "LockedToDiscordGuildId",
                table: "Linkshells",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }
    }
}
