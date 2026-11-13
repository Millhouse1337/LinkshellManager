using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Loot: the loot history (add, edit, delete loot rows) and auctions (create, edit, start, bid,
// close, history). Split out of ActivityDataController; the endpoints, URLs and rules are
// unchanged.
[ApiController]
[Route("api/activity")]
public sealed partial class ActivityLootController : ActivityControllerBase
{
    private readonly TimeZoneConversionService _timeZones;
    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;
    private readonly DkpPoolBalanceService _dkpPoolBalances;
    private readonly TreasuryBalanceService _treasury;
    private readonly TreasuryJournalWriter _treasuryJournal;

    public ActivityLootController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        ILogger<ActivityLootController> logger,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        TreasuryBalanceService treasury,
        TreasuryJournalWriter treasuryJournal,
        LinkshellPermissions permissions)
        : base(dbContext, discordIdentityService, userManager, logger, permissions)
    {
        _timeZones = timeZones;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _dkpPoolBalances = dkpPoolBalances;
        _treasury = treasury;
        _treasuryJournal = treasuryJournal;
    }
}
