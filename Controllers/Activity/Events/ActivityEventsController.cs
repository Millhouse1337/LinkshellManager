using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Timed events from start to finish: create, start, end and delete (Lifecycle), sign-up and
// loot (Participation), breaks, comments, the party board and party setups. Split out of
// ActivityDataController; the endpoints, URLs and rules are unchanged.
[ApiController]
[Route("api/activity")]
public sealed partial class ActivityEventsController : ActivityControllerBase
{
    private readonly TimeZoneConversionService _timeZones;
    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;
    private readonly DkpPoolBalanceService _dkpPoolBalances;
    private readonly MonsterTimingResolver _monsterTimings;

    public ActivityEventsController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        ILogger<ActivityEventsController> logger,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        MonsterTimingResolver monsterTimings,
        LinkshellPermissions permissions)
        : base(dbContext, discordIdentityService, userManager, logger, permissions)
    {
        _timeZones = timeZones;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _dkpPoolBalances = dkpPoolBalances;
        _monsterTimings = monsterTimings;
    }
}
