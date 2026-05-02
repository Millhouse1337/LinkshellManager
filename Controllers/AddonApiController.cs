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
    private const string AddonSource = "att-addon";

    private readonly ApplicationDbContext _dbContext;
    private readonly AddonApiAuthService _auth;
    private readonly UserManager<AppUser> _userManager;
    private readonly DiscordIdentityService _discordIdentityService;
    private readonly IHostEnvironment _environment;

    public AddonApiController(
        ApplicationDbContext dbContext,
        AddonApiAuthService auth,
        UserManager<AppUser> userManager,
        DiscordIdentityService discordIdentityService,
        IHostEnvironment environment)
    {
        _dbContext = dbContext;
        _auth = auth;
        _userManager = userManager;
        _discordIdentityService = discordIdentityService;
        _environment = environment;
    }

    // Dual-auth resolver for the management endpoints: tries the Discord OAuth
    // bearer token first (so the Activity SPA can call them) and falls back to
    // ASP.NET Identity cookie auth (used by the MVC web app's Customize page).
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
                // Fall through to cookie auth.
            }
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
        if (string.IsNullOrEmpty(token.IssuedToAppUserId)) return false;
        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(m => m.LinkshellId == linkshellId && m.AppUserId == token.IssuedToAppUserId, cancellationToken);
        if (membership is null) return false;
        var rank = string.IsNullOrWhiteSpace(membership.Rank) ? "Member" : membership.Rank.Trim();
        var role = await _dbContext.LinkshellRoles
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == rank, cancellationToken);
        if (role is null)
        {
            role = await _dbContext.LinkshellRoles
                .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == "Member", cancellationToken);
        }
        return role?.CanModerateLiveEvent == true;
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
        if (membership is null || string.IsNullOrWhiteSpace(membership.Rank))
        {
            return false;
        }
        return membership.Rank.Equals("Leader", StringComparison.OrdinalIgnoreCase)
            || membership.Rank.Equals("Officer", StringComparison.OrdinalIgnoreCase);
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
        int? WindowCount = null);

    public sealed record AddonAttendanceRequest(
        DateTime? RecordedAtUtc,
        List<AddonAttendanceEntry> Entries,
        int? WindowSequence = null);

    public sealed record AddonBreakRequest(int ParticipantId);

    public sealed record AddonVerifyReturnRequest(int LedgerEntryId);

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
        string? CapturedMessage);

    public sealed record AddonPostLootRequest(
        string ItemName,
        string ItemWinner,
        int? WinningDkpSpent);
}
