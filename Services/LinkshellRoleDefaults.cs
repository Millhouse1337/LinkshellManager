using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

public static class LinkshellRoleDefaults
{
    public static IEnumerable<LinkshellRole> BuildDefaultRoles(int linkshellId)
    {
        yield return new LinkshellRole
        {
            LinkshellId = linkshellId,
            Name = "Leader",
            IsSystem = true,
            SortOrder = 0,
            CanManageRoles = true,
            CanManageMembers = true,
            CanManageEvents = true,
            CanModerateLiveEvent = true,
            CanAddLoot = true,
            CanManageInventory = true,
            CanManageTreasury = true,
            CanManageRules = true,
            CanManageAnnouncements = true,
            CanManageTods = true,
            CanAuditDkp = true,
            CanManageAuctions = true,
            CanCustomizeLinkshell = true
        };

        yield return new LinkshellRole
        {
            LinkshellId = linkshellId,
            Name = "Officer",
            IsSystem = true,
            SortOrder = 1,
            CanManageRoles = false,
            CanManageMembers = false,
            CanManageEvents = true,
            CanModerateLiveEvent = true,
            CanAddLoot = true,
            CanManageInventory = true,
            CanManageTreasury = false,
            CanManageRules = true,
            CanManageAnnouncements = true,
            CanManageTods = true,
            CanAuditDkp = false,
            CanManageAuctions = true,
            CanCustomizeLinkshell = false
        };

        yield return new LinkshellRole
        {
            LinkshellId = linkshellId,
            Name = "Member",
            IsSystem = true,
            SortOrder = 2
        };
    }
}
