using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// HNM camps and their records: window events (camp cards, captures, Post), window posts, the HNM
// board and claim stats, ToDs (log, edit, delete, screenshots), upcoming repops, monster timings
// and the pending-submission queue.
[ApiController]
[Route("api/activity")]
public sealed partial class ActivityWindowEventsController : ActivityControllerBase
{
    private readonly TimeZoneConversionService _timeZones;
    private readonly WindowEventDkpLedgerService _windowEventDkpLedger;
    private readonly WindowEventLinkService _windowEventLinks;
    private readonly MonsterTimingResolver _monsterTimings;
    private readonly MonsterTimingEditor _monsterTimingEditor;
    private readonly LinkshellMonsterTimingProvisioner _monsterTimingProvisioner;

    public ActivityWindowEventsController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        WindowEventDkpLedgerService windowEventDkpLedger,
        WindowEventLinkService windowEventLinks,
        ILogger<ActivityWindowEventsController> logger,
        MonsterTimingResolver monsterTimings,
        MonsterTimingEditor monsterTimingEditor,
        LinkshellMonsterTimingProvisioner monsterTimingProvisioner,
        LinkshellPermissions permissions)
        : base(dbContext, discordIdentityService, userManager, logger, permissions)
    {
        _timeZones = timeZones;
        _windowEventDkpLedger = windowEventDkpLedger;
        _windowEventLinks = windowEventLinks;
        _monsterTimings = monsterTimings;
        _monsterTimingEditor = monsterTimingEditor;
        _monsterTimingProvisioner = monsterTimingProvisioner;
    }
}
