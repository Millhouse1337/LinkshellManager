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

        var result = await EventController.EndEventCoreAsync(_dbContext, eventEntity);

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
