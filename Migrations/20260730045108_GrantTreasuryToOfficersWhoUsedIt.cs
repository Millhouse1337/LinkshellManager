using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkshellManagerDiscordApp.Migrations
{
    /// <summary>
    /// Preserves treasury write access for the officers who were actually using it.
    ///
    /// The same RevenueEntries table was writable through two doors with different locks: the web
    /// Manage Revenue form checked LinkshellRanks.IsLeaderOrOfficer (a coarse rank), while the
    /// Activity API checked the granular LinkshellRole.CanManageTreasury flag. Because
    /// LinkshellRoleDefaults gives Officer CanManageTreasury = false, an officer could record gil
    /// through the web that the API would have refused — a privilege escalation available by
    /// picking a front-end.
    ///
    /// Unifying on the granular flag is the correct resolution, but on its own it would silently
    /// revoke access some officers use every week. A blanket grant is the other wrong answer: it
    /// would hand treasury write access to officers in linkshells whose leader never intended it.
    /// So this grants the flag only where the evidence says it was being exercised — a linkshell
    /// where an officer-ranked member actually recorded an entry through the web form.
    ///
    /// The four EntryType values below are exactly ManageRevenueViewModel.SourceOptions, so they
    /// could only have come from that form. Item sales ("Income" / "Item Sale") are gated on
    /// CanManageInventory instead, and the Activity's "Income"/"Expense" rows already required the
    /// flag, so neither is evidence of anything.
    ///
    /// Needs a release note: "Recording treasury entries now needs the 'Manage treasury' permission
    /// on both the website and Discord. Officers who had already recorded entries keep access;
    /// leaders can grant it to anyone else under Linkshell → Permissions."
    /// </summary>
    public partial class GrantTreasuryToOfficersWhoUsedIt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "LinkshellRoles" AS role
                SET "CanManageTreasury" = TRUE
                WHERE lower(role."Name") = 'officer'
                  AND role."IsSystem" = TRUE
                  AND role."CanManageTreasury" = FALSE
                  AND EXISTS (
                      SELECT 1
                      FROM "RevenueEntries" AS entry
                      JOIN "AppUserLinkshells" AS membership
                        ON membership."LinkshellId" = entry."LinkshellId"
                       AND membership."AppUserId" = entry."CreatedByAppUserId"
                      WHERE entry."LinkshellId" = role."LinkshellId"
                        AND entry."EntryType" IN ('AH Sale', 'Mercenary Work', 'Donation', 'Other')
                        AND lower(membership."Rank") = 'officer'
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately a no-op. Revoking would mean clearing CanManageTreasury on every system
            // Officer role, including ones a leader had already ticked on by hand — and this
            // migration cannot tell those apart from the ones it set. Leaving the grants in place on
            // a rollback errs in the safe direction: it over-permits by exactly the set that was
            // already over-permitted through the web form before this shipped.
        }
    }
}
