using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    // Maps Postgres's built-in xmin as a concurrency token on Events and AppUserLinkshells
    // (Event.Version, AppUserLinkshell.Version). xmin is a system column every Postgres row already
    // has, so there is nothing to create or drop: this migration only moves the model snapshot.
    //
    // EF scaffolds it as AddColumn("xmin"). Npgsql would skip that for a system column anyway, but
    // the body is left empty on purpose so nothing here can ever touch a real column.
    /// <inheritdoc />
    public partial class AddXminConcurrencyTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
