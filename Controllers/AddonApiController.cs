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
public sealed class AddonApiController : ControllerBase
{
    private const string AddonSource = "att-addon";

    private readonly ApplicationDbContext _dbContext;
    private readonly AddonApiAuthService _auth;
    private readonly UserManager<AppUser> _userManager;

    public AddonApiController(
        ApplicationDbContext dbContext,
        AddonApiAuthService auth,
        UserManager<AppUser> userManager)
    {
        _dbContext = dbContext;
        _auth = auth;
        _userManager = userManager;
    }

    // ---------------------------------------------------------------------
    // Addon-facing endpoints (token auth)
    // ---------------------------------------------------------------------

    [HttpPost("pair")]
    public async Task<IActionResult> PairAsync([FromBody] PairRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new { error = "Pairing code is required." });
        }

        var result = await _auth.RedeemPairingCodeAsync(request.Code, cancellationToken);
        if (result is null)
        {
            return BadRequest(new { error = "Pairing code is invalid, expired, or already used." });
        }

        return Ok(new
        {
            token = result.RawToken,
            linkshellId = result.Linkshell.Id,
            linkshellName = result.Linkshell.LinkshellName,
            label = result.Record.Label
        });
    }

    [HttpGet("me")]
    [AddonApiAuth]
    public async Task<IActionResult> MeAsync(CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var linkshell = await _dbContext.Linkshells
            .FirstOrDefaultAsync(ls => ls.Id == token.LinkshellId, cancellationToken);

        string? issuedToCharacterName = null;
        if (!string.IsNullOrEmpty(token.IssuedToAppUserId))
        {
            var membership = await _dbContext.AppUserLinkshells
                .FirstOrDefaultAsync(
                    m => m.LinkshellId == token.LinkshellId && m.AppUserId == token.IssuedToAppUserId,
                    cancellationToken);
            issuedToCharacterName = membership?.CharacterName;
        }

        return Ok(new
        {
            linkshellId = token.LinkshellId,
            linkshellName = linkshell?.LinkshellName,
            issuedToCharacterName,
            label = token.Label
        });
    }

    [HttpGet("events")]
    [AddonApiAuth]
    public async Task<IActionResult> ListEventsAsync(CancellationToken cancellationToken)
    {
        var linkshellId = AddonApiAuthAttribute.GetLinkshellId(HttpContext);

        var events = await _dbContext.Events
            .Where(evt => evt.LinkshellId == linkshellId)
            .OrderByDescending(evt => evt.CommencementStartTime)
            .ThenByDescending(evt => evt.StartTime)
            .Take(50)
            .Select(evt => new
            {
                id = evt.Id,
                name = evt.EventName,
                type = evt.EventType,
                location = evt.EventLocation,
                startTime = evt.StartTime,
                commencementStartTime = evt.CommencementStartTime,
                isLive = evt.CommencementStartTime != null && evt.EndTime == null
            })
            .ToListAsync(cancellationToken);

        return Ok(new { events });
    }

    [HttpPost("events")]
    [AddonApiAuth]
    public async Task<IActionResult> CreateEventAsync(
        [FromBody] AddonCreateEventRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Event name is required." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = DateTime.UtcNow;

        var eventEntity = new Event
        {
            LinkshellId = token.LinkshellId,
            EventName = request.Name.Trim(),
            EventType = string.IsNullOrWhiteSpace(request.Type) ? null : request.Type.Trim(),
            EventLocation = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim(),
            CreatorUserId = token.IssuedToAppUserId,
            StartTime = request.StartUtc ?? nowUtc,
            CommencementStartTime = nowUtc,
            DkpPerHour = request.DkpPerHour,
            Details = string.IsNullOrWhiteSpace(request.Details) ? "Created from att addon." : request.Details.Trim(),
            TimeStamp = nowUtc
        };

        _dbContext.Events.Add(eventEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            eventId = eventEntity.Id,
            name = eventEntity.EventName,
            commencementStartTime = eventEntity.CommencementStartTime
        });
    }

    [HttpPost("events/{eventId:int}/start")]
    [AddonApiAuth]
    public async Task<IActionResult> StartEventAsync(int eventId, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "Event not found." });
        }
        if (eventEntity.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }

        var alreadyStarted = eventEntity.CommencementStartTime is not null;
        if (!alreadyStarted)
        {
            eventEntity.CommencementStartTime = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Ok(new
        {
            eventId = eventEntity.Id,
            name = eventEntity.EventName,
            commencementStartTime = eventEntity.CommencementStartTime,
            alreadyStarted
        });
    }

    [HttpPost("events/{eventId:int}/attendance")]
    [AddonApiAuth]
    public async Task<IActionResult> PostAttendanceAsync(
        int eventId,
        [FromBody] AddonAttendanceRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Entries is null || request.Entries.Count == 0)
        {
            return BadRequest(new { error = "At least one attendance entry is required." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = request.RecordedAtUtc ?? DateTime.UtcNow;

        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "Event not found." });
        }

        if (eventEntity.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }

        // Auto-commence the event if it hasn't started yet (so attendance has a meaningful base time).
        if (eventEntity.CommencementStartTime is null)
        {
            eventEntity.CommencementStartTime = nowUtc;
        }

        var verifiedBy = (token.Label ?? "att-addon") + " (att)";

        var matched = 0;
        var alreadyVerified = 0;
        var unmatched = new List<string>();
        var ledgerIds = new List<int>();

        // Pre-load all linkshell memberships in one query so we can match without a roundtrip per entry.
        var memberships = await _dbContext.AppUserLinkshells
            .Where(m => m.LinkshellId == token.LinkshellId && m.AppUserId != null)
            .ToListAsync(cancellationToken);

        var membershipByName = memberships
            .Where(m => !string.IsNullOrWhiteSpace(m.CharacterName))
            .GroupBy(m => m.CharacterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in request.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.CharacterName)) continue;

            var name = entry.CharacterName.Trim();
            if (!membershipByName.TryGetValue(name, out var membership))
            {
                unmatched.Add(name);
                continue;
            }

            var existing = await _dbContext.AppUserEvents
                .FirstOrDefaultAsync(
                    ue => ue.EventId == eventId && ue.AppUserId == membership.AppUserId,
                    cancellationToken);

            AppUserEvent participation;
            if (existing is null)
            {
                participation = new AppUserEvent
                {
                    AppUserId = membership.AppUserId,
                    EventId = eventId,
                    CharacterName = membership.CharacterName,
                    JobName = string.IsNullOrWhiteSpace(entry.MainJob) ? null : entry.MainJob.Trim(),
                    SubJobName = string.IsNullOrWhiteSpace(entry.SubJob) ? null : entry.SubJob.Trim(),
                    JobType = null,
                    StartTime = nowUtc,
                    IsQuickJoin = true,
                    IsVerified = true
                };
                _dbContext.AppUserEvents.Add(participation);
                await _dbContext.SaveChangesAsync(cancellationToken);
                matched++;
            }
            else
            {
                participation = existing;
                if (participation.IsVerified == true)
                {
                    alreadyVerified++;
                    continue;
                }

                participation.IsVerified = true;
                if (participation.StartTime is null) participation.StartTime = nowUtc;
                if (string.IsNullOrWhiteSpace(participation.JobName) && !string.IsNullOrWhiteSpace(entry.MainJob))
                {
                    participation.JobName = entry.MainJob.Trim();
                }
                if (string.IsNullOrWhiteSpace(participation.SubJobName) && !string.IsNullOrWhiteSpace(entry.SubJob))
                {
                    participation.SubJobName = entry.SubJob.Trim();
                }
                matched++;
            }

            var ledger = new AppUserEventStatusLedger
            {
                AppUserEventId = participation.Id,
                EventId = eventId,
                AppUserId = membership.AppUserId,
                ActionType = "Verify",
                OccurredAt = nowUtc,
                RequiresVerification = false,
                VerifiedAt = nowUtc,
                VerifiedBy = verifiedBy,
                Source = AddonSource
            };
            _dbContext.AppUserEventStatusLedgers.Add(ledger);
            await _dbContext.SaveChangesAsync(cancellationToken);
            ledgerIds.Add(ledger.Id);
        }

        return Ok(new
        {
            matched,
            alreadyVerified,
            unmatched,
            ledgerEntryIds = ledgerIds
        });
    }

    // ---------------------------------------------------------------------
    // Management endpoints (Identity cookie / Discord bearer auth)
    // Used by the website to issue and revoke addon tokens.
    // ---------------------------------------------------------------------

    [HttpPost("management/pairing-code")]
    [Authorize]
    public async Task<IActionResult> CreatePairingCodeAsync(
        [FromBody] CreatePairingCodeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LinkshellId <= 0)
        {
            return BadRequest(new { error = "Linkshell is required." });
        }

        var appUser = await _userManager.GetUserAsync(User);
        if (appUser is null)
        {
            return Unauthorized();
        }

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.AppUserId == appUser.Id && m.LinkshellId == request.LinkshellId,
                cancellationToken);

        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var code = await _auth.CreatePairingCodeAsync(
            request.LinkshellId, appUser.Id, request.Label, cancellationToken);

        return Ok(new
        {
            code,
            expiresInMinutes = 10
        });
    }

    [HttpGet("management/tokens")]
    [Authorize]
    public async Task<IActionResult> ListTokensAsync(
        [FromQuery] int linkshellId,
        CancellationToken cancellationToken)
    {
        if (linkshellId <= 0)
        {
            return BadRequest(new { error = "Linkshell is required." });
        }

        var appUser = await _userManager.GetUserAsync(User);
        if (appUser is null) return Unauthorized();

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.AppUserId == appUser.Id && m.LinkshellId == linkshellId,
                cancellationToken);

        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var tokens = await _auth.ListActiveAsync(linkshellId, cancellationToken);
        return Ok(new
        {
            tokens = tokens.Select(t => new
            {
                id = t.Id,
                prefix = t.TokenPrefix,
                label = t.Label,
                createdAt = t.CreatedAt,
                lastUsedAt = t.LastUsedAt,
                issuedToAppUserId = t.IssuedToAppUserId
            })
        });
    }

    [HttpPost("management/tokens/{tokenId:int}/revoke")]
    [Authorize]
    public async Task<IActionResult> RevokeTokenAsync(
        int tokenId,
        [FromQuery] int linkshellId,
        CancellationToken cancellationToken)
    {
        if (linkshellId <= 0)
        {
            return BadRequest(new { error = "Linkshell is required." });
        }

        var appUser = await _userManager.GetUserAsync(User);
        if (appUser is null) return Unauthorized();

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.AppUserId == appUser.Id && m.LinkshellId == linkshellId,
                cancellationToken);

        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var revoked = await _auth.RevokeAsync(tokenId, linkshellId, cancellationToken);
        if (!revoked)
        {
            return NotFound(new { error = "Token not found." });
        }

        return Ok(new { success = true });
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
        string? Details);

    public sealed record AddonAttendanceRequest(
        DateTime? RecordedAtUtc,
        List<AddonAttendanceEntry> Entries);

    public sealed record AddonAttendanceEntry(
        string CharacterName,
        string? MainJob,
        string? SubJob,
        string? Zone);

    public sealed record CreatePairingCodeRequest(
        int LinkshellId,
        string? Label);
}
