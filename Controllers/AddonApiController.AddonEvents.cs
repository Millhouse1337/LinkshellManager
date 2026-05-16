using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class AddonApiController
{
    [HttpGet("events")]
    [AddonApiAuth]
    public async Task<IActionResult> ListEventsAsync(CancellationToken cancellationToken)
    {
        var linkshellId = AddonApiAuthAttribute.GetLinkshellId(HttpContext);

        var query = _dbContext.Events
            .Where(evt => evt.LinkshellId == linkshellId);

        var raw = await query
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
                dkpPerHour = evt.DkpPerHour,
                windowCountOverride = evt.WindowCountOverride,
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
            evt.dkpPerHour,
            // Explicit per-event override beats the name-based lookup so a
            // user-named event flagged as "Claim/Kill" reports 2 windows.
            windowCount = evt.windowCountOverride ?? HnmConfig.GetWindowCount(evt.name),
            createdFromAddon = evt.creationSource == "Addon"
                || (evt.creationSource is null
                    && (evt.details ?? string.Empty)
                        .StartsWith("Created from addon.", StringComparison.Ordinal))
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
        if (!await TokenIssuerCanModerateAsync(token, token.LinkshellId, cancellationToken))
        {
            return Forbid();
        }

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
            Details = string.IsNullOrWhiteSpace(request.Details) ? "Created from lsm addon." : request.Details.Trim(),
            CreationSource = "Addon",
            // Persist explicit window-count override only when the addon
            // actually picked a multi-post style. Storing >=2 keeps the
            // column meaningful (1 = default, no need to record).
            WindowCountOverride = request.WindowCount is > 1 ? request.WindowCount : null,
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

    // PATCH /api/addon/events/{eventId}
    //
    // Updates a live or queued event's DKP rate from the addon. The intended
    // use case: an officer has an HNM event running, opens the att Settings
    // popup, changes "HNM - DKP / Window" from 1 to 5, and clicks Save —
    // the next window posted should credit 5 DKP, not the value the event
    // was created with. The PostAttendance flow reads eventEntity.DkpPerHour
    // when crediting per-window DKP, so the simplest path is to update that
    // column whenever the user explicitly saves new defaults.
    //
    // The single DkpPerHour column is reused for both event flavors — the
    // addon picks which of its two defaults (dkpPerHourRegular vs
    // dkpPerWindowHnm) to send based on the event's window count.
    //
    // Idempotent: setting the same value is a no-op and returns 200.
    [HttpPatch("events/{eventId:int}")]
    [AddonApiAuth]
    public async Task<IActionResult> UpdateEventAsync(
        int eventId,
        [FromBody] AddonUpdateEventRequest request,
        CancellationToken cancellationToken)
    {
        if (request.DkpPerHour is null)
        {
            return BadRequest(new { error = "dkpPerHour is required." });
        }
        if (request.DkpPerHour < 0)
        {
            return BadRequest(new { error = "dkpPerHour must be non-negative." });
        }

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
        if (!await TokenIssuerCanModerateAsync(token, eventEntity.LinkshellId, cancellationToken))
        {
            return Forbid();
        }
        if (eventEntity.EndTime is not null)
        {
            return BadRequest(new { error = "Cannot edit a closed event." });
        }

        eventEntity.DkpPerHour = request.DkpPerHour;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            id = eventEntity.Id,
            dkpPerHour = eventEntity.DkpPerHour
        });
    }

    public sealed record AddonUpdateEventRequest(int? DkpPerHour);

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
        if (!await TokenIssuerCanModerateAsync(token, eventToDelete.LinkshellId, cancellationToken))
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
        if (!await TokenIssuerCanModerateAsync(token, eventEntity.LinkshellId, cancellationToken))
        {
            return Forbid();
        }

        var alreadyStarted = eventEntity.CommencementStartTime is not null;
        if (!alreadyStarted)
        {
            eventEntity.CommencementStartTime = DateTime.UtcNow;
            eventEntity.StarterUserId ??= token.IssuedToAppUserId;
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
        if (!await TokenIssuerCanModerateAsync(token, eventEntity.LinkshellId, cancellationToken))
        {
            return Forbid();
        }

        var result = await EventController.EndEventCoreAsync(_dbContext, eventEntity);
        var windowCount = eventEntity.WindowCountOverride ?? HnmConfig.GetWindowCount(eventEntity.EventName);
        if (windowCount <= 1)
        {
            await _sheetSync.EnqueueEventCloseAsync(eventEntity.Id, cancellationToken);
        }
        if (result.HasLootDeductions)
        {
            await _sheetSync.EnqueueEventLootDeductionsAsync(result.EventHistoryId, cancellationToken);
        }

        // For windowed events DkpPerHour is reused as DkpPerWindow (same column,
        // different semantic). Surface both names so the addon can format the
        // chat output correctly without inferring from windowCount alone.
        return Ok(new
        {
            eventId               = eventId,
            eventName             = eventEntity.EventName,
            eventType             = eventEntity.EventType,
            eventLocation         = eventEntity.EventLocation,
            commencementStartTime = eventEntity.CommencementStartTime,
            endTime               = result.EndTimeUtc,
            dkpPerHour            = result.WindowCount > 1 ? (int?)null : eventEntity.DkpPerHour,
            dkpPerWindow          = result.WindowCount > 1 ? eventEntity.DkpPerHour : (int?)null,
            windowCount           = result.WindowCount,
            participants          = result.Participants.Select(p => new
            {
                characterName    = p.CharacterName,
                jobName          = p.JobName,
                subJobName       = p.SubJobName,
                durationHours    = p.DurationHours,
                windowsAttended  = p.WindowsAttended,
                dkpEarned        = p.DkpEarned
            })
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
            windowCount = eventEntity.WindowCountOverride ?? HnmConfig.GetWindowCount(eventEntity.EventName),
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
}
