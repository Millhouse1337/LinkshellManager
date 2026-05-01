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

        var raw = await _dbContext.Events
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
                isLive = evt.CommencementStartTime != null && evt.EndTime == null,
                creationSource = evt.CreationSource,
                details = evt.Details
            })
            .ToListAsync(cancellationToken);

        // windowCount is computed in-process from the event name so it stays in sync
        // with HnmConfig (server is authoritative; addon falls back to its local table).
        // createdFromAddon is sourced from the explicit CreationSource column. Legacy
        // rows (CreationSource null) fall back to a Details-prefix check so events
        // created before the column existed still show the cancel button if they
        // were originally posted by the addon.
        var events = raw.Select(evt => new
        {
            evt.id,
            evt.name,
            evt.type,
            evt.location,
            evt.startTime,
            evt.commencementStartTime,
            evt.isLive,
            windowCount = HnmConfig.GetWindowCount(evt.name),
            createdFromAddon = evt.creationSource == "Addon"
                || (evt.creationSource is null
                    && (evt.details ?? string.Empty)
                        .StartsWith("Created from att addon.", StringComparison.Ordinal))
        });

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

        // Note: CommencementStartTime is intentionally null on create so the event
        // appears as "Queued". Callers that want to start immediately should follow
        // up with POST /api/addon/events/{id}/start.
        var eventEntity = new Event
        {
            LinkshellId = token.LinkshellId,
            EventName = request.Name.Trim(),
            EventType = string.IsNullOrWhiteSpace(request.Type) ? null : request.Type.Trim(),
            EventLocation = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim(),
            CreatorUserId = token.IssuedToAppUserId,
            StartTime = request.StartUtc ?? nowUtc,
            DkpPerHour = request.DkpPerHour,
            Details = string.IsNullOrWhiteSpace(request.Details) ? "Created from att addon." : request.Details.Trim(),
            CreationSource = "Addon",
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

    [HttpDelete("events/{eventId:int}")]
    [AddonApiAuth]
    public async Task<IActionResult> CancelEventAsync(int eventId, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var eventToDelete = await _dbContext.Events
            .Include(evt => evt.Jobs)
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.EventLootDetails)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventToDelete is null)
        {
            return NotFound(new { error = "Event not found." });
        }
        if (eventToDelete.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }
        if (eventToDelete.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Live events cannot be canceled. End the event instead." });
        }

        _dbContext.Jobs.RemoveRange(eventToDelete.Jobs);
        _dbContext.AppUserEvents.RemoveRange(eventToDelete.AppUserEvents);
        _dbContext.EventLootDetails.RemoveRange(eventToDelete.EventLootDetails);
        _dbContext.Events.Remove(eventToDelete);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { canceled = eventId });
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

    // Ends a running event from the addon. Mirrors the web app's EndEvent
    // flow (writes EventHistory + DkpLedgerEntry rows, removes the live
    // Event / Jobs / participants / loot details). Reuses the same internal
    // helper so the DKP math stays in lockstep with the web path.
    [HttpPost("events/{eventId:int}/end")]
    [AddonApiAuth]
    public async Task<IActionResult> EndEventAsync(int eventId, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var eventEntity = await _dbContext.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.EventLootDetails)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "Event not found." });
        }
        if (eventEntity.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }

        await EventController.EndEventCoreAsync(_dbContext, eventEntity);

        return Ok(new
        {
            eventId   = eventId,
            eventName = eventEntity.EventName
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

        // For HNM events the addon may pin this batch to a specific spawn window
        // (1..GetWindowCount). Find or create the EventAttendanceWindow row up front
        // so per-user inserts below can attach to it.
        EventAttendanceWindow? attendanceWindow = null;
        if (request.WindowSequence is int windowSequence)
        {
            var maxWindows = HnmConfig.GetWindowCount(eventEntity.EventName);
            if (windowSequence < 1 || windowSequence > maxWindows)
            {
                return BadRequest(new { error = $"Window sequence {windowSequence} is out of range for this event (max {maxWindows})." });
            }

            attendanceWindow = await _dbContext.EventAttendanceWindows
                .FirstOrDefaultAsync(
                    w => w.EventId == eventId && w.SequenceNumber == windowSequence,
                    cancellationToken);

            if (attendanceWindow is null)
            {
                attendanceWindow = new EventAttendanceWindow
                {
                    EventId = eventId,
                    SequenceNumber = windowSequence,
                    Label = HnmConfig.GetDefaultWindowLabel(eventEntity.EventName, windowSequence),
                    PostedAt = nowUtc,
                    PostedBySource = AddonSource
                };
                _dbContext.EventAttendanceWindows.Add(attendanceWindow);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
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

            // For windowed events we count "matched" per posted window so re-posting
            // the same name to a different window still bumps the count.
            // For non-windowed events we keep the legacy "first verify wins" behavior.
            var firstTimeVerified = existing is null || existing.IsVerified != true;

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
            }
            else
            {
                participation = existing;
                if (participation.IsVerified == true && attendanceWindow is null)
                {
                    // Legacy single-window event: re-posting an already-verified user is a no-op.
                    alreadyVerified++;
                    continue;
                }

                if (participation.IsVerified != true)
                {
                    participation.IsVerified = true;
                    if (participation.StartTime is null) participation.StartTime = nowUtc;
                }
                if (string.IsNullOrWhiteSpace(participation.JobName) && !string.IsNullOrWhiteSpace(entry.MainJob))
                {
                    participation.JobName = entry.MainJob.Trim();
                }
                if (string.IsNullOrWhiteSpace(participation.SubJobName) && !string.IsNullOrWhiteSpace(entry.SubJob))
                {
                    participation.SubJobName = entry.SubJob.Trim();
                }
            }

            // Per-window join row: silently skip if the user was already credited for this window.
            if (attendanceWindow is not null)
            {
                var alreadyAttendedThisWindow = await _dbContext.AppUserEventWindows
                    .AnyAsync(
                        w => w.AppUserEventId == participation.Id
                          && w.EventAttendanceWindowId == attendanceWindow.Id,
                        cancellationToken);

                if (alreadyAttendedThisWindow)
                {
                    alreadyVerified++;
                    continue;
                }

                _dbContext.AppUserEventWindows.Add(new AppUserEventWindow
                {
                    AppUserEventId = participation.Id,
                    EventAttendanceWindowId = attendanceWindow.Id,
                    VerifiedAt = nowUtc,
                    VerifiedBy = verifiedBy,
                    Zone = string.IsNullOrWhiteSpace(entry.Zone) ? null : entry.Zone.Trim()
                });
            }

            matched++;

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
                Source = AddonSource,
                EventAttendanceWindowId = attendanceWindow?.Id
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
            ledgerEntryIds = ledgerIds,
            windowSequence = attendanceWindow?.SequenceNumber,
            windowId = attendanceWindow?.Id,
            windowLabel = attendanceWindow?.Label
        });
    }

    // Returns one event (with its posted attendance windows + per-window attendees)
    // so the addon can rehydrate its in-memory window cache after a reload. Cheaper
    // than re-fetching all events when the user just wants to re-select one.
    [HttpGet("events/{eventId:int}")]
    [AddonApiAuth]
    public async Task<IActionResult> GetEventAsync(int eventId, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var eventEntity = await _dbContext.Events
            .Include(e => e.AttendanceWindows)
                .ThenInclude(w => w.Attendees)
                    .ThenInclude(a => a.AppUserEvent)
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);

        if (eventEntity is null) return NotFound(new { error = "Event not found." });
        if (eventEntity.LinkshellId != token.LinkshellId) return Forbid();

        return Ok(new
        {
            id = eventEntity.Id,
            name = eventEntity.EventName,
            type = eventEntity.EventType,
            location = eventEntity.EventLocation,
            startTime = eventEntity.StartTime,
            commencementStartTime = eventEntity.CommencementStartTime,
            isLive = eventEntity.CommencementStartTime != null && eventEntity.EndTime == null,
            windowCount = HnmConfig.GetWindowCount(eventEntity.EventName),
            windows = eventEntity.AttendanceWindows
                .OrderBy(w => w.SequenceNumber)
                .Select(w => new
                {
                    sequenceNumber = w.SequenceNumber,
                    label = w.Label,
                    postedAt = w.PostedAt,
                    attendees = w.Attendees
                        .OrderBy(a => a.AppUserEvent != null ? a.AppUserEvent.CharacterName : string.Empty)
                        .Select(a => new
                        {
                            id = a.Id,
                            characterName = a.AppUserEvent != null ? a.AppUserEvent.CharacterName : null,
                            jobName = a.AppUserEvent != null ? a.AppUserEvent.JobName : null,
                            subJobName = a.AppUserEvent != null ? a.AppUserEvent.SubJobName : null,
                            zone = a.Zone,
                            verifiedAt = a.VerifiedAt
                        })
                        .ToList()
                })
                .ToList()
        });
    }

    // Token-authenticated remove: lets the addon undo an accidental window post for
    // a single character without nuking the whole event. The path uses the same shape
    // as the other addon endpoints (event id + window sequence) and matches the
    // attendee by character name (case-insensitive on the trimmed value).
    [HttpDelete("events/{eventId:int}/windows/{sequence:int}/attendees/{characterName}")]
    [AddonApiAuth]
    public async Task<IActionResult> RemoveWindowAttendeeAddonAsync(
        int eventId, int sequence, string characterName, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var attendanceWindow = await _dbContext.EventAttendanceWindows
            .Include(w => w.Event)
            .Include(w => w.Attendees)
                .ThenInclude(a => a.AppUserEvent)
            .FirstOrDefaultAsync(
                w => w.EventId == eventId && w.SequenceNumber == sequence,
                cancellationToken);

        if (attendanceWindow is null) return NotFound(new { error = "Window not found." });
        if (attendanceWindow.Event is null || attendanceWindow.Event.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }

        var trimmed = (characterName ?? string.Empty).Trim();
        var attendee = attendanceWindow.Attendees.FirstOrDefault(a =>
            a.AppUserEvent != null
            && string.Equals(a.AppUserEvent.CharacterName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (attendee is null) return NotFound(new { error = "Attendee not found in this window." });

        await RemoveWindowAttendeeRowAsync(attendee, cancellationToken);
        return Ok(new { removedId = attendee.Id });
    }

    // ---------------------------------------------------------------------
    // Management endpoints (Identity cookie / Discord bearer auth)
    // Used by the website to issue and revoke addon tokens.
    // ---------------------------------------------------------------------

    // Cookie-authenticated remove: the web /Event/Start page and the Discord
    // Activity's Attendance Windows card both call this with the AppUserEventWindow
    // row id (exposed via their respective view models / DTOs).
    [HttpDelete("management/window-attendees/{id:int}")]
    [Authorize]
    public async Task<IActionResult> RemoveWindowAttendeeManagementAsync(
        int id, CancellationToken cancellationToken)
    {
        var appUser = await _userManager.GetUserAsync(User);
        if (appUser is null) return Unauthorized();

        var attendee = await _dbContext.AppUserEventWindows
            .Include(a => a.EventAttendanceWindow)
                .ThenInclude(w => w!.Event)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (attendee is null) return NotFound(new { error = "Attendee not found." });
        var linkshellId = attendee.EventAttendanceWindow?.Event?.LinkshellId;
        if (linkshellId is null) return NotFound(new { error = "Window event not found." });

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.AppUserId == appUser.Id && m.LinkshellId == linkshellId.Value,
                cancellationToken);
        if (!CanManageLinkshell(membership)) return Forbid();

        await RemoveWindowAttendeeRowAsync(attendee, cancellationToken);
        return Ok(new { removedId = attendee.Id });
    }

    // Returns the linkshell roster + its loot structure so the addon can
    // populate the Winner combo on the Loot Pool panel and label the DKP
    // field correctly (raw DKP for "Dkp", percentage for "Hybrid",
    // disabled for "LootCouncil"). Token-scoped: the user only sees the
    // roster of the linkshell their pairing is bound to.
    [HttpGet("roster")]
    [AddonApiAuth]
    public async Task<IActionResult> GetRosterAsync(CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var linkshell = await _dbContext.Linkshells
            .FirstOrDefaultAsync(ls => ls.Id == token.LinkshellId, cancellationToken);

        var characterNames = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == token.LinkshellId
                        && !string.IsNullOrWhiteSpace(link.CharacterName))
            .Select(link => link.CharacterName!)
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            characterNames,
            lootStructure = ActivityDataController.NormalizeLootStructure(linkshell?.LootStructure ?? "Dkp")
        });
    }

    // Posts a single TodLootDetail row attached to an existing Tod. Mirrors
    // the Discord Activity create-tod-loot flow but accepts one item per
    // call (matches the per-row "Post Loot" UX in the addon launcher) and
    // is gated by addon-token auth on the parent Tod's linkshell.
    [HttpPost("tod/{todId:int}/loot")]
    [AddonApiAuth]
    public async Task<IActionResult> PostTodLootAsync(
        int todId,
        [FromBody] AddonPostLootRequest request,
        CancellationToken cancellationToken)
    {
        var itemName   = request.ItemName?.Trim();
        var itemWinner = request.ItemWinner?.Trim();
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return BadRequest(new { error = "Item name is required." });
        }
        if (string.IsNullOrWhiteSpace(itemWinner))
        {
            return BadRequest(new { error = "Item winner is required." });
        }
        if (!request.WinningDkpSpent.HasValue || request.WinningDkpSpent.Value <= 0)
        {
            return BadRequest(new { error = "WinningDkpSpent must be a positive number." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = DateTime.UtcNow;

        var tod = await _dbContext.Tods
            .Include(t => t.Linkshell)
            .Include(t => t.TodLootDetails)
            .FirstOrDefaultAsync(t => t.Id == todId, cancellationToken);
        if (tod is null || tod.LinkshellId != token.LinkshellId)
        {
            return NotFound(new { error = "Tod not found." });
        }

        var lootStructure = ActivityDataController.NormalizeLootStructure(tod.Linkshell?.LootStructure ?? "Dkp");
        if (lootStructure == "LootCouncil")
        {
            return BadRequest(new { error = "Linkshell uses LootCouncil — DKP loot posts are disabled." });
        }
        if (lootStructure == "Hybrid" && request.WinningDkpSpent.Value > 100)
        {
            return BadRequest(new { error = "Deduction % cannot exceed 100." });
        }

        // Validate winner is in the linkshell's roster (case-insensitive match
        // on CharacterName) — same guard CreateTodAsync uses.
        var rosterMatch = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == token.LinkshellId
                        && link.CharacterName != null
                        && link.CharacterName.ToLower() == itemWinner.ToLower())
            .Select(link => link.CharacterName!)
            .FirstOrDefaultAsync(cancellationToken);
        if (rosterMatch is null)
        {
            return BadRequest(new { error = "Winner must be a current linkshell member." });
        }

        var detail = new TodLootDetail
        {
            TodId = tod.Id,
            ItemName = itemName,
            ItemWinner = rosterMatch,
            WinningDkpSpent = request.WinningDkpSpent
        };

        _dbContext.TodLootDetails.Add(detail);
        await ActivityDataController.AdjustTodLootDkpAsync(
            _dbContext, tod, new[] { detail }, nowUtc, isRefund: false, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            lootDetailId      = detail.Id,
            itemName          = detail.ItemName,
            itemWinner        = detail.ItemWinner,
            winningDkpSpent   = detail.WinningDkpSpent,
            actualDeductedDkp = detail.ActualDeductedDkp
        });
    }

    // Addon-side ToD posting. Mirrors the Discord Activity create-tod path in
    // ActivityDataController but with addon-token auth (linkshell-scoped) and
    // a slimmer payload — the addon only knows the monster name and the
    // wall-clock kill time, so cooldown/interval defaults are filled in
    // server-side using the same helpers the Discord Activity uses.
    [HttpPost("tod")]
    [AddonApiAuth]
    public async Task<IActionResult> PostTodAsync(
        [FromBody] AddonPostTodRequest request,
        CancellationToken cancellationToken)
    {
        var monsterName = request.MonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            return BadRequest(new { error = "Monster name is required." });
        }

        if (!request.DefeatedAtUtc.HasValue)
        {
            return BadRequest(new { error = "DefeatedAtUtc is required." });
        }

        var defeatedAtUtc = DateTime.SpecifyKind(request.DefeatedAtUtc.Value, DateTimeKind.Utc);
        if (defeatedAtUtc > DateTime.UtcNow.AddMinutes(5))
        {
            return BadRequest(new { error = "DefeatedAtUtc cannot be in the future." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = DateTime.UtcNow;

        var cooldown = ActivityDataController.GetDefaultTodCooldown(monsterName);
        var interval = ActivityDataController.GetDefaultTodInterval(monsterName);
        var repopTimeUtc = defeatedAtUtc.AddHours(ActivityDataController.ResolveTodCooldownHours(cooldown));

        var tod = new Tod
        {
            LinkshellId = token.LinkshellId,
            MonsterName = monsterName,
            DayNumber = null,
            Claim = false,
            Time = defeatedAtUtc,
            Cooldown = cooldown,
            RepopTime = repopTimeUtc,
            Interval = interval,
            TimeStamp = nowUtc,
            TotalTods = 1,
            TotalClaims = 0,
            ImagePath = null
        };

        _dbContext.Tods.Add(tod);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            todId = tod.Id,
            monsterName = tod.MonsterName,
            defeatedAtUtc = tod.Time,
            repopTimeUtc = tod.RepopTime,
            cooldown = tod.Cooldown,
            interval = tod.Interval
        });
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
        List<AddonAttendanceEntry> Entries,
        int? WindowSequence = null);

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
