using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

public static class LinkshellRoleDefaults
{
    // An in-memory, NEVER-persisted role with every permission granted. The admin
    // override hands this to callers that are shaped around a LinkshellRole (the
    // per-controller GetEffectiveRoleAsync helpers, the sidebar's ViewData
    // permissions, the Activity permissions mapper) so the override flows through
    // the existing plumbing instead of special-casing each one.
    // Do NOT add this to the DbContext — Id stays 0 and it has no matching row.
    public static LinkshellRole BuildFullAccessRole(int linkshellId) => new()
    {
        LinkshellId = linkshellId,
        Name = AdminRoleName,
        IsSystem = true,
        SortOrder = -1,
        CanManageRoles = true,
        CanManageMembers = true,
        CanManageEvents = true,
        CanModerateLiveEvent = true,
        CanAddLoot = true,
        CanManageInventory = true,
        CanManageCharts = true,
        CanManageTreasury = true,
        CanManageRules = true,
        CanManageAnnouncements = true,
        CanManageTods = true,
        CanAuditDkp = true,
        CanManageAuctions = true,
        CanLockAuctions = true,
        CanCustomizeLinkshell = true,
        CanSubmitTodForApproval = true,
        CanSubmitAttendanceForApproval = true,
        CanManageParties = true,
        CanManageInvites = true,
        CanBid = true
    };

    // Label for the synthetic full-access role above. Not a rank anyone can be
    // assigned — AppUserLinkshell.Rank is never set to this.
    public const string AdminRoleName = "Admin";

    public static IEnumerable<LinkshellRole> BuildDefaultRoles(int linkshellId)
    {
        yield return new LinkshellRole
        {
            LinkshellId = linkshellId,
            Name = LinkshellRanks.Leader,
            IsSystem = true,
            SortOrder = 0,
            CanManageRoles = true,
            CanManageMembers = true,
            CanManageEvents = true,
            CanModerateLiveEvent = true,
            CanAddLoot = true,
            CanManageInventory = true,
            CanManageCharts = true,
            CanManageTreasury = true,
            CanManageRules = true,
            CanManageAnnouncements = true,
            CanManageTods = true,
            CanAuditDkp = true,
            CanManageAuctions = true,
            CanLockAuctions = true,
            CanCustomizeLinkshell = true,
            CanSubmitTodForApproval = true,
            CanSubmitAttendanceForApproval = true,
            CanManageParties = true,
            CanManageInvites = true,
            CanBid = true
        };

        yield return new LinkshellRole
        {
            LinkshellId = linkshellId,
            Name = LinkshellRanks.Officer,
            IsSystem = true,
            SortOrder = 1,
            CanManageRoles = false,
            CanManageMembers = false,
            CanManageEvents = true,
            CanModerateLiveEvent = true,
            CanAddLoot = true,
            CanManageInventory = true,
            // Charts is a shared operational board like ToDs / Events / Rules — on for officers by
            // default. It is not a money surface like Treasury, where withholding is the point.
            CanManageCharts = true,
            CanManageTreasury = false,
            CanManageRules = true,
            CanManageAnnouncements = true,
            CanManageTods = true,
            CanAuditDkp = false,
            CanManageAuctions = true,
            CanLockAuctions = true,
            CanCustomizeLinkshell = false,
            CanSubmitTodForApproval = true,
            CanSubmitAttendanceForApproval = true,
            CanManageParties = true,
            CanManageInvites = true,
            CanBid = true
        };

        yield return new LinkshellRole
        {
            LinkshellId = linkshellId,
            Name = LinkshellRanks.Member,
            IsSystem = true,
            SortOrder = 2,
            // Regular members can bid; all management permissions stay off.
            CanBid = true
        };

        // Probationary rank: same (lack of) management access as Member, but no
        // bidding. Leaders can flip CanBid on later if they want to graduate
        // bidding separately from the full Member rank.
        yield return new LinkshellRole
        {
            LinkshellId = linkshellId,
            Name = LinkshellRanks.Trial,
            IsSystem = true,
            SortOrder = 3,
            CanBid = false
        };
    }
}
