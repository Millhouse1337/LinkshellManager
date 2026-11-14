using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// The Activity's home screens: the overview (linkshells, events, DKP at a glance), the live
// change feed, the member's profile, the one-time download links and the banner.
[ApiController]
[Route("api/activity")]
public sealed partial class ActivityOverviewController : ActivityControllerBase
{
    private readonly AppUserProfileService _appUserProfileService;
    private readonly TimeZoneConversionService _timeZones;
    private readonly DkpSheetService _dkpSheet;
    private readonly GlobalSettingsService _globalSettings;
    private readonly AdminOverrideService _adminOverride;
    private readonly DkpPoolResolver _dkpPools;
    private readonly DkpPoolBalanceService _dkpPoolBalances;
    private readonly TreasuryBalanceService _treasury;
    private readonly MonsterTimingResolver _monsterTimings;

    public ActivityOverviewController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        AppUserProfileService appUserProfileService,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        DkpSheetService dkpSheet,
        ILogger<ActivityOverviewController> logger,
        GlobalSettingsService globalSettings,
        AdminOverrideService adminOverride,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        TreasuryBalanceService treasury,
        MonsterTimingResolver monsterTimings,
        LinkshellPermissions permissions)
        : base(dbContext, discordIdentityService, userManager, logger, permissions)
    {
        _appUserProfileService = appUserProfileService;
        _timeZones = timeZones;
        _dkpSheet = dkpSheet;
        _globalSettings = globalSettings;
        _adminOverride = adminOverride;
        _dkpPools = dkpPools;
        _dkpPoolBalances = dkpPoolBalances;
        _treasury = treasury;
        _monsterTimings = monsterTimings;
    }
}
