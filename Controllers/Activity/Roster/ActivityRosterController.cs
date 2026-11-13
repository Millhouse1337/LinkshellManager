using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// The roster: members (remove, rank, status), roles and their permissions, invites and join
// requests, claiming imported DKP, job ratings and the jobs roster. Split out of
// ActivityDataController; the endpoints, URLs and rules are unchanged.
[ApiController]
[Route("api/activity")]
public sealed partial class ActivityRosterController : ActivityControllerBase
{
    private readonly InviteCandidateService _inviteCandidates;
    private readonly AdminOverrideService _adminOverride;

    public ActivityRosterController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        InviteCandidateService inviteCandidates,
        UserManager<AppUser> userManager,
        ILogger<ActivityRosterController> logger,
        AdminOverrideService adminOverride,
        LinkshellPermissions permissions)
        : base(dbContext, discordIdentityService, userManager, logger, permissions)
    {
        _inviteCandidates = inviteCandidates;
        _adminOverride = adminOverride;
    }
}
