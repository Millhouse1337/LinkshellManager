using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLinkshellType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Linkshell Type radio (Timed Events Only / Snapshot Only / Both) is gone — the
            // Feature toggles own section visibility now, and every linkshell gets the full
            // "Both" experience. Before dropping the column, carry the one decision it still
            // made that a toggle can't infer: a "Timed Events Only" linkshell had its HNM
            // content hidden by TYPE while EnableHnmSection sat at its default true. Fold that
            // into the toggle so those linkshells don't suddenly sprout an HNM tab; they can
            // turn it back on from Customize → Feature toggles → HNM tab.
            migrationBuilder.Sql(
                """
                UPDATE "Linkshells"
                SET "EnableHnmSection" = FALSE
                WHERE "LinkshellType" = 'SkySeaDynamis';
                """);

            migrationBuilder.DropColumn(
                name: "LinkshellType",
                table: "Linkshells");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LinkshellType",
                table: "Linkshells",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // "Both" rather than "" so a rollback lands on the everything-visible
                // value the app is collapsing to, not an unknown one.
                defaultValue: "Both");
        }
    }
}
