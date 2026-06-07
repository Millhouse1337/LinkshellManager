using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <inheritdoc />
    public partial class BackfillManageInvitesPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // CanManageInvites was added (MergeReconcile) with defaultValue:false
            // and no backfill, so every pre-existing system Leader/Officer role
            // row got `false` even though the seed defaults grant it. Role seeding
            // only inserts missing role *names* — it never upgrades existing rows —
            // so without this one-time backfill, leaders/officers of older
            // linkshells get a (legitimate) Forbid on every invite action
            // (revoke/send/approve), which the Activity surfaced as a crash.
            migrationBuilder.Sql(
                @"UPDATE ""LinkshellRoles""
                  SET ""CanManageInvites"" = true
                  WHERE ""IsSystem"" = true AND ""Name"" IN ('Leader', 'Officer');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data backfill — intentionally irreversible. Reverting could not
            // distinguish rows we set from rows an admin deliberately enabled.
        }
    }
}
