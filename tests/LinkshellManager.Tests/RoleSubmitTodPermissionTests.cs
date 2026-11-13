using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Models;
using Xunit;

namespace LinkshellManager.Tests;

// "Submit ToDs for approval" in the role editor. The flag existed (the ToD create path and the addon
// read it) but no save ever wrote it, so a leader could not let members send ToDs for approval.
//
// Its request field is nullable on purpose: a browser tab still running the editor from before the
// toggle existed sends nothing for it, and that save must not switch it off on Leader/Officer.
public class RoleSubmitTodPermissionTests
{
    private static ActivityLinkshellRolePermissions Request(bool? canSubmitTod) => new(
        Name: "Member",
        CanManageRoles: false,
        CanManageMembers: false,
        CanManageEvents: false,
        CanModerateLiveEvent: false,
        CanAddLoot: false,
        CanManageInventory: false,
        CanManageCharts: false,
        CanManageTreasury: false,
        CanManageRules: false,
        CanManageAnnouncements: false,
        CanManageTods: false,
        CanAuditDkp: false,
        CanManageAuctions: false,
        CanLockAuctions: false,
        CanCustomizeLinkshell: false,
        CanManageParties: false,
        CanManageInvites: false,
        CanSubmitTodForApproval: canSubmitTod);

    [Fact]
    public void Save_WritesTheFlagWhenSent()
    {
        var role = new LinkshellRole { Name = "Member" };

        ActivityRosterController.ApplyPermissions(role, Request(true));
        Assert.True(role.CanSubmitTodForApproval);

        ActivityRosterController.ApplyPermissions(role, Request(false));
        Assert.False(role.CanSubmitTodForApproval);
    }

    [Fact]
    public void Save_FromAnOlderClient_LeavesTheFlagAlone()
    {
        var role = new LinkshellRole { Name = "Officer", CanSubmitTodForApproval = true };

        ActivityRosterController.ApplyPermissions(role, Request(null));

        Assert.True(role.CanSubmitTodForApproval);
    }
}
