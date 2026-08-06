using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[ApiController]
[Route("api/addon")]
public sealed partial class AddonApiController : ControllerBase
{
    private const string AddonSource = "lsm-addon";

    private readonly ApplicationDbContext _dbContext;
    private readonly AddonApiAuthService _auth;
    private readonly UserManager<AppUser> _userManager;
    private readonly DiscordIdentityService _discordIdentityService;
    private readonly IHostEnvironment _environment;
    private readonly HnmAutoEventService _hnmAutoEvent;
    private readonly DiscordWebhookQueue _discordWebhook;
    private readonly DiscordEventChannelQueue _eventQueue;
    private readonly GlobalSettingsService _globalSettings;
    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;
    private readonly DkpPoolBalanceService _dkpPoolBalances;
    private readonly ILogger<AddonApiController> _logger;

    public AddonApiController(
        ApplicationDbContext dbContext,
        AddonApiAuthService auth,
        UserManager<AppUser> userManager,
        DiscordIdentityService discordIdentityService,
        IHostEnvironment environment,
        HnmAutoEventService hnmAutoEvent,
        DiscordWebhookQueue discordWebhook,
        DiscordEventChannelQueue eventQueue,
        GlobalSettingsService globalSettings,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        ILogger<AddonApiController> logger)
    {
        _dbContext = dbContext;
        _auth = auth;
        _userManager = userManager;
        _discordIdentityService = discordIdentityService;
        _environment = environment;
        _hnmAutoEvent = hnmAutoEvent;
        _discordWebhook = discordWebhook;
        _eventQueue = eventQueue;
        _globalSettings = globalSettings;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _dkpPoolBalances = dkpPoolBalances;
        _logger = logger;
    }

    // Dual-auth resolver for the management endpoints: tries the Discord OAuth
    // bearer token first (so the Activity SPA can call them) and otherwise
    // uses ASP.NET Identity cookie auth (used by the MVC web app's Customize
    // page). When a bearer token is presented but fails validation, the
    // request is rejected — we do NOT silently downgrade to cookie auth, since
    // an attacker who can disrupt outbound calls to discord.com would
    // otherwise be able to force the cookie path and exploit any CSRF gap.
    private async Task<AppUser?> ResolveManagementUserAsync(CancellationToken cancellationToken)
    {
        if (TryGetManagementBearerToken(out var accessToken))
        {
            try
            {
                var localUser = await _discordIdentityService.GetCurrentLocalUserAsync(accessToken, cancellationToken);
                if (!string.IsNullOrWhiteSpace(localUser.AppUser?.Id))
                {
                    return await _userManager.FindByIdAsync(localUser.AppUser.Id);
                }
            }
            catch (DiscordApiException)
            {
                // Hard reject — never fall through to cookie auth when a
                // bearer token is presented.
            }
            return null;
        }

        if (User.Identity?.IsAuthenticated == true)
        {
            return await _userManager.GetUserAsync(User);
        }

        return null;
    }

    private bool TryGetManagementBearerToken(out string accessToken)
    {
        accessToken = string.Empty;

        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var headerValue))
        {
            return false;
        }
        if (!"Bearer".Equals(headerValue.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(headerValue.Parameter))
        {
            return false;
        }

        // Skip addon att_ tokens — those are handled by [AddonApiAuth] on
        // separate endpoints. Only Discord OAuth tokens flow through here.
        if (headerValue.Parameter.StartsWith("att_", StringComparison.Ordinal))
        {
            return false;
        }

        accessToken = headerValue.Parameter;
        return true;
    }

    private sealed class BreakActionContext
    {
        public AddonApiToken? Token { get; set; }
        public Event? EventEntity { get; set; }
        public AppUserEvent? Participation { get; set; }
        public bool IsModeratorAction { get; set; }
        public IActionResult? Error { get; set; }
    }

    private async Task<BreakActionContext> ResolveBreakContextAsync(
        int eventId, int participantId, CancellationToken cancellationToken)
    {
        var ctx = new BreakActionContext { Token = AddonApiAuthAttribute.GetToken(HttpContext) };
        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null) { ctx.Error = NotFound(new { error = "Event not found." }); return ctx; }
        if (eventEntity.LinkshellId != ctx.Token!.LinkshellId) { ctx.Error = Forbid(); return ctx; }
        if (!eventEntity.CommencementStartTime.HasValue)
        {
            ctx.Error = BadRequest(new { error = "Break status is only available after the event has started." });
            return ctx;
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(p => p.Id == participantId && p.EventId == eventId, cancellationToken);
        if (participation is null) { ctx.Error = NotFound(new { error = "Participant not found." }); return ctx; }

        var isSelf = !string.IsNullOrEmpty(ctx.Token.IssuedToAppUserId)
            && string.Equals(participation.AppUserId, ctx.Token.IssuedToAppUserId, StringComparison.OrdinalIgnoreCase);
        var canModerate = await TokenIssuerCanModerateAsync(ctx.Token, eventEntity.LinkshellId, cancellationToken);
        if (!isSelf && !canModerate) { ctx.Error = Forbid(); return ctx; }

        ctx.EventEntity = eventEntity;
        ctx.Participation = participation;
        ctx.IsModeratorAction = !isSelf && canModerate;
        return ctx;
    }

    private async Task<bool> TokenIssuerCanModerateAsync(
        AddonApiToken token, int linkshellId, CancellationToken cancellationToken)
    {
        var role = await GetTokenIssuerRoleAsync(token, linkshellId, cancellationToken);
        return role?.CanModerateLiveEvent == true;
    }

    // Returns the LinkshellRole record for the AppUser that issued the token,
    // or null if the AppUser is no longer a member of the linkshell. Callers
    // inspect the returned role to evaluate Can* permissions.
    private async Task<LinkshellRole?> GetTokenIssuerRoleAsync(
        AddonApiToken token, int linkshellId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token.IssuedToAppUserId)) return null;
        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(m => m.LinkshellId == linkshellId && m.AppUserId == token.IssuedToAppUserId, cancellationToken);
        if (membership is null) return null;
        var rank = string.IsNullOrWhiteSpace(membership.Rank) ? LinkshellRanks.Member : membership.Rank.Trim();
        var role = await _dbContext.LinkshellRoles
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == rank, cancellationToken);
        if (role is null)
        {
            role = await _dbContext.LinkshellRoles
                .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == LinkshellRanks.Member, cancellationToken);
        }
        return role;
    }

    private async Task<string?> ResolveTokenIssuerNameAsync(
        AddonApiToken token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token.IssuedToAppUserId)) return token.Label;
        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(m => m.LinkshellId == token.LinkshellId && m.AppUserId == token.IssuedToAppUserId, cancellationToken);
        return membership?.CharacterName ?? token.Label;
    }

    // Shared deletion path: drop the join row plus any matching ledger entries that
    // recorded the original verify, so re-posting the same attendee posts cleanly.
    private async Task RemoveWindowAttendeeRowAsync(
        AppUserEventWindow attendee, CancellationToken cancellationToken)
    {
        var ledgerEntries = await _dbContext.AppUserEventStatusLedgers
            .Where(l => l.EventAttendanceWindowId == attendee.EventAttendanceWindowId
                     && l.AppUserEventId == attendee.AppUserEventId
                     && l.ActionType == "Verify")
            .ToListAsync(cancellationToken);
        if (ledgerEntries.Count > 0)
        {
            _dbContext.AppUserEventStatusLedgers.RemoveRange(ledgerEntries);
        }

        _dbContext.AppUserEventWindows.Remove(attendee);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool CanManageLinkshell(AppUserLinkshell? membership)
    {
        return LinkshellRanks.IsLeaderOrOfficer(membership?.Rank);
    }

    // ---------------------------------------------------------------------
    // DTOs
    // ---------------------------------------------------------------------

    public sealed record PairRequest(string Code);

    public sealed record AddonCreateEventRequest(
        string Name,
        string? Type,
        string? Location,
        DateTime? StartUtc,
        int? DkpPerHour,
        string? Details,
        int? WindowCount = null,
        // Optional reusable party-setup template to attach. Setting this is
        // what makes the event's Discord post carry a sign-up board -- the
        // same column and the same effect as picking one on the website.
        int? PartySetupId = null,
        // HNM only. The COMBINED "Base/Stronger" label ("Behemoth/King Behemoth"),
        // matching what Event.AssignedMonsterName stores everywhere else, plus the
        // day cycle. Null falls back to reading both out of Name -- older addon
        // builds send neither, and the name has always carried them as text.
        string? MonsterName = null,
        int? DayNumber = null);

    public sealed record AddonAttendanceRequest(
        DateTime? RecordedAtUtc,
        List<AddonAttendanceEntry> Entries,
        int? WindowSequence = null,
        // The addon's currently-logged-in character name. Used to identify
        // which entry represents the token issuer so we can backfill their
        // linkshell membership / AppUser CharacterName on first attendance
        // post when those columns were left blank at signup time.
        string? SelfCharacterName = null,
        // True when this came from the addon's ARMED automatic per-window capture rather than an
        // officer clicking Post. Moderators-only: a non-moderator sending this is refused outright
        // instead of being queued for approval, so a revoked role can't flood the approval queue
        // with one submission per window. Defaults false, so every existing caller is unaffected.
        bool Auto = false,
        // What this window pays each attendee, from the officer's "Dkp this window" box. Rides on
        // the post because this request is what CREATES the window row — see the write in
        // PostAttendanceAsync. Null means "leave the window at whatever price it already has",
        // which is what an automatic capture and every older addon build both send.
        double? WindowDkp = null);

    public sealed record AddonBreakRequest(int ParticipantId);

    public sealed record AddonVerifyReturnRequest(int LedgerEntryId);

    // Late-join body for POST /events/{id}/join. JobType ("Main"/"Sub"/etc.)
    // is optional because the addon scan doesn't always carry it; the server
    // stores null when unset which matches the existing per-row data shape
    // from PostAttendance.
    public sealed record AddonJoinEventRequest(
        string? MainJob,
        string? SubJob,
        string? JobType);

    public sealed record AddonAttendanceEntry(
        string CharacterName,
        string? MainJob,
        string? SubJob,
        string? Zone);

    public sealed record CreatePairingCodeRequest(
        int LinkshellId,
        string? Label);

    public sealed record AddonPostTodRequest(
        string MonsterName,
        DateTime? DefeatedAtUtc,
        string? CapturedMessage,
        // Tri-state: true=Claimed, false=Unclaimed, null=Not Specified
        // (auto-posted from the loot-pool flow before the user picked).
        bool? Claim,
        // Day cycle counter for day-tracked HNMs (Nidhogg/King Behemoth/
        // Aspidochelone). The addon's launcher captures this via
        // state.eventPresetDayInputs. Null/0 for non-day-tracked monsters
        // and for HNMs where the officer left the day input blank.
        int? DayNumber);

    public sealed record AddonUpdateTodClaimRequest(bool? Claim);

    public sealed record AddonPostLootRequest(
        string ItemName,
        string ItemWinner,
        int? WinningDkpSpent);

    // Per-character job-level array published by the LSM addon so other
    // installations can see who can equip a given drop. Index = FFXI job id
    // (0..21), value = level (0 if unleveled).
    public sealed record AddonPostJobLevelsRequest(
        string CharacterName,
        int[] JobLevels);
}
