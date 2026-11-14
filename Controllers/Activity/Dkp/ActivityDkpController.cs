using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// DKP: the DKP history and audit (manual add/remove), DKP pools, the DKP import, the DKP sheet and
// past events (event history and its edits).
[ApiController]
[Route("api/activity")]
public sealed partial class ActivityDkpController : ActivityControllerBase
{
    private readonly DkpSheetService _dkpSheet;
    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;
    private readonly DkpPoolEditor _dkpPoolEditor;
    private readonly DkpPoolEventTypeCatalog _dkpPoolEventTypes;

    public ActivityDkpController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        UserManager<AppUser> userManager,
        DkpSheetService dkpSheet,
        ILogger<ActivityDkpController> logger,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        DkpPoolEditor dkpPoolEditor,
        DkpPoolEventTypeCatalog dkpPoolEventTypes,
        LinkshellPermissions permissions)
        : base(dbContext, discordIdentityService, userManager, logger, permissions)
    {
        _dkpSheet = dkpSheet;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _dkpPoolEditor = dkpPoolEditor;
        _dkpPoolEventTypes = dkpPoolEventTypes;
    }
}
