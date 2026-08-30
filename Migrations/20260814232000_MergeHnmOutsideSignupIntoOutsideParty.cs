using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class MergeHnmOutsideSignupIntoOutsideParty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two switches collapse into one. HnmOutsideSignupEnabled was doing two jobs and has
            // lost both: it gated whether the HNM event type could be created at all (obsolete now
            // that HNM camps are ordinary events), and it was a second, HNM-only copy of the
            // account-less signup gate. OutsidePartySignupEnabled now covers every event type.
            //
            // OR the two rather than just dropping the column: a linkshell running HNM camps with
            // HNM Outside Sign Up on and Outside Party Signup off is the common shape, and dropping
            // the column bare would silently lock its account-less members out of Check In. The
            // permissive value is the one that preserves what those linkshells have working today;
            // a leader who wants it closed can turn the one switch off in Customize.
            migrationBuilder.Sql(
                """
                UPDATE "Linkshells"
                SET "OutsidePartySignupEnabled" = TRUE
                WHERE "HnmOutsideSignupEnabled" = TRUE;
                """);

            migrationBuilder.DropColumn(
                name: "HnmOutsideSignupEnabled",
                table: "Linkshells");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HnmOutsideSignupEnabled",
                table: "Linkshells",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The merge is lossy going forward (two bits became one), so the rollback restores the
            // only reading that keeps HNM working: whatever the surviving switch says. A linkshell
            // that had HNM open before the merge gets it back.
            migrationBuilder.Sql(
                """
                UPDATE "Linkshells"
                SET "HnmOutsideSignupEnabled" = "OutsidePartySignupEnabled";
                """);
        }
    }
}
