using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Linkshell settings: create, update, delete and leave a linkshell, its Discord server, lock and
// channels, plus its rules, announcements and items. Split out of ActivityDataController; the
// endpoints, URLs and rules are unchanged.
[ApiController]
[Route("api/activity")]
public sealed partial class ActivitySettingsController : ActivityControllerBase
{
    private readonly DiscordBotClient _discordBot;
    private readonly AdminOverrideService _adminOverride;
    private readonly MemberActivityService _memberActivity;
    private readonly ChannelRouteEditor _channelRoutes;
    private readonly ItemSaleRecorder _itemSales;
    private readonly MonsterTimingResolver _monsterTimings;

    public ActivitySettingsController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        DiscordBotClient discordBot,
        UserManager<AppUser> userManager,
        ILogger<ActivitySettingsController> logger,
        AdminOverrideService adminOverride,
        MemberActivityService memberActivity,
        ChannelRouteEditor channelRoutes,
        ItemSaleRecorder itemSales,
        MonsterTimingResolver monsterTimings,
        LinkshellPermissions permissions)
        : base(dbContext, discordIdentityService, userManager, logger, permissions)
    {
        _discordBot = discordBot;
        _adminOverride = adminOverride;
        _memberActivity = memberActivity;
        _channelRoutes = channelRoutes;
        _itemSales = itemSales;
        _monsterTimings = monsterTimings;
    }

    private static bool IsValidLootStructure(string? structure)
    {
        return string.Equals(structure, "Dkp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(structure, "LootCouncil", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(structure, "Hybrid", StringComparison.OrdinalIgnoreCase);
    }
}
