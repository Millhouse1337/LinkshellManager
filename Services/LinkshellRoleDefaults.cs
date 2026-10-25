using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

public static class LinkshellRoleDefaults
{
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
            CanManageParties = true,
            CanManageInvites = true,
            CanBid = true,
            CanUseAddon = true,
            CanManageAttendanceWithAddon = true
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
            CanManageParties = true,
            CanManageInvites = true,
            CanBid = true,
            CanUseAddon = true,
            CanManageAttendanceWithAddon = true
        };

        yield return new LinkshellRole
        {
            LinkshellId = linkshellId,
            Name = LinkshellRanks.Member,
            IsSystem = true,
            SortOrder = 2,
            // Regular members can bid; all management permissions stay off. The addon is not a
            // management permission -- a member scanning the roster they are standing in is the
            // whole point of it.
            CanBid = true,
            CanUseAddon = true
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
            CanBid = false,
            // On even here: a trial member is still at the camp, and their scans are how they are
            // credited for it.
            CanUseAddon = true
        };
    }
}
