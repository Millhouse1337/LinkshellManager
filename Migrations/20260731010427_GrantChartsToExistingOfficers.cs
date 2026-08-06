using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// Gives the built-in Officer role the new Charts permission on linkshells that already exist.
    ///
    /// This is NOT the GrantTreasuryToOfficersWhoUsedIt case: no access is being revoked and there is
    /// no prior usage to condition on, because the feature ships with this release. It exists because
    /// LinkshellRoleDefaults gives new linkshells' Officers CanManageCharts = true, and
    /// EnsureDefaultRolesForLinkshellsAsync only ever inserts MISSING roles — it never back-fills a
    /// new column onto an existing row. Without this, whether your officers can edit the Sky chart
    /// would depend on when your linkshell was created, which is not a rule anyone could discover.
    ///
    /// Scoped to the IsSystem roles named leader/officer. Custom roles a leader built by hand are
    /// left alone: their permission set is a deliberate statement and this migration has no standing
    /// to edit it. A Sky pop-item list is also not gil — over-granting it costs a wrong name in a
    /// table, not a missing 3,000,000.
    ///
    /// The leader half is cosmetic — CanAsync short-circuits for Leader — but leaving it FALSE would
    /// show a leader an unticked box in the Permissions editor for something they demonstrably have.
    /// </summary>
    public partial class GrantChartsToExistingOfficers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "LinkshellRoles"
                SET "CanManageCharts" = TRUE
                WHERE "IsSystem" = TRUE
                  AND lower("Name") IN ('leader', 'officer');
                """);
        }

        /// <inheritdoc />
        // Deliberately a no-op, for the same reason GrantTreasuryToOfficersWhoUsedIt's is: this
        // cannot tell its own grants apart from ones a leader ticked by hand afterwards.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
