using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Past-event (EventHistory) browsing + editing for the Discord Activity. Mirrors
// the web EventHistory page: any member can view; leaders/officers (CanManageEvents)
// can edit metadata/DKP, correct an attendee's DKP, or remove an attendee. DKP
// recompute is delegated to EventHistoryEditService (same logic as the web).
public sealed partial class ActivityDataController
{
    // Constructed per call rather than injected — it's stateless apart from the scoped services it
    // wraps, and every DKP move it makes goes through the same DkpLedgerWriter this request uses,
    // so pool attribution and the running balance stay consistent with the rest of the request.
    private EventHistoryEditService EventHistoryEdits() => new(_dbContext, _dkpLedger, _dkpPools);

    [HttpGet("event-history")]
    public async Task<IActionResult> GetEventHistoryAsync([FromQuery] int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to view event history." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var canManage = await CanAsync(membership, r => r.CanManageEvents, cancellationToken);

        // Settled events only, matching EventController.BuildPastEventsAsync -- an ended camp
        // archives at End Camp, so without this it appears here while its DKP is still unposted.
        // Events with no Window Event (every ordinary timed event) are unaffected.
        var histories = await _dbContext.EventHistories
            .AsNoTracking()
            .Include(h => h.AppUserEventHistories)
            .Where(h => h.LinkshellId == linkshellId)
            .Where(h => !_dbContext.WindowEvents.Any(
                w => w.CampEventHistoryId == h.Id && w.PostedToSheetAt == null))
            .OrderByDescending(h => h.EndTime ?? h.TimeStamp)
            .Take(100)
            .ToListAsync(cancellationToken);

        // Whole-roster support: per event, surface members who did NOT attend so the
        // client can mark them Absent (default) or add them with DKP. Roster loaded once.
        var roster = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId && m.AppUserId != null)
            .Select(m => new
            {
                AppUserId = m.AppUserId!,
                Name = m.CharacterName ?? m.AppUser!.CharacterName ?? m.AppUser!.UserName,
                Alt1 = m.AppUser!.AltCharacterName1,
                Alt2 = m.AppUser!.AltCharacterName2
            })
            .ToListAsync(cancellationToken);

        // Each member's alt character names, by account, so both attendees and
        // absentees can show "(alt1, alt2)" next to the displayed name. The
        // displayed name itself is filtered out so a member who signed up as an
        // alt doesn't list that same name as one of their alts.
        var altsByUser = roster.ToDictionary(
            m => m.AppUserId,
            m => new[] { m.Alt1, m.Alt2 }
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim())
                .ToArray(),
            StringComparer.Ordinal);
        string[] AltsFor(string? appUserId, string? displayName)
        {
            if (appUserId is null || !altsByUser.TryGetValue(appUserId, out var alts)) return Array.Empty<string>();
            return alts.Where(n => !string.Equals(n, displayName, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        // How many attendance windows each closed event archived. Counts only — the windows
        // themselves (and their rosters) load lazily per card from GetEventHistoryWindowsAsync
        // below, because a page of 25-window wyrm camps carries more roster rows than the entire
        // rest of this payload.
        var archivedWindowCounts = await EventHistoryWindowsReader.CountsByHistoryAsync(
            _dbContext, histories.Select(h => h.Id).ToList(), cancellationToken);

        var result = histories.Select(h =>
        {
            var attendeeIds = h.AppUserEventHistories
                .Where(p => p.AppUserId != null)
                .Select(p => p.AppUserId!)
                .ToHashSet(StringComparer.Ordinal);
            return new
            {
                id = h.Id,
                eventName = h.EventName,
                eventType = h.EventType,
                eventLocation = h.EventLocation,
                startTime = h.StartTime,
                endTime = h.EndTime,
                duration = h.Duration,
                dkpPerHour = h.DkpPerHour,
                eventDkp = h.EventDkp,
                // > 0 marks this as a windowed (HNM-style) camp with a surviving window record,
                // which is what the card branches on to offer its Attendance Windows section.
                // Always 0 for events closed before the archive existed.
                archivedWindowCount = archivedWindowCounts.GetValueOrDefault(h.Id, 0),
                participants = h.AppUserEventHistories
                    .OrderBy(p => p.CharacterName)
                    .Select(p => new
                    {
                        id = p.Id,
                        appUserId = p.AppUserId,
                        characterName = p.CharacterName,
                        altNames = AltsFor(p.AppUserId, p.CharacterName),
                        jobName = p.JobName,
                        subJobName = p.SubJobName,
                        duration = p.Duration,
                        eventDkp = p.EventDkp,
                        activeCredit = p.ActiveCredit,
                        // Null on a timed event; the window tally on a windowed one, which is what
                        // that member's DKP was actually computed from.
                        windowsAttended = p.WindowsAttended
                    }),
                absentees = roster
                    .Where(m => !attendeeIds.Contains(m.AppUserId))
                    .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new { appUserId = m.AppUserId, characterName = m.Name, altNames = AltsFor(m.AppUserId, m.Name) })
            };
        });

        return Ok(new { canManage, histories = result });
    }

    // The archived attendance windows for one closed camp, loaded on demand when a card expands.
    // Read-only and membership-gated (not officer-gated) — a member should be able to see which
    // windows they were scanned in, the same as they can see the attendee list.
    [HttpGet("event-history/{id:int}/windows")]
    public async Task<IActionResult> GetEventHistoryWindowsAsync(int id, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to view event history." });
        }

        var history = await _dbContext.EventHistories
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (history is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, history.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var archive = await EventHistoryWindowsReader.LoadAsync(_dbContext, history, cancellationToken);

        return Ok(new
        {
            windowCount = archive.WindowCount,
            distinctAttendeeCount = archive.DistinctAttendeeCount,
            windows = archive.Windows.Select(window => new
            {
                id = window.Id,
                sequenceNumber = window.SequenceNumber,
                label = window.Label,
                postedAt = window.PostedAt,
                postedBySource = window.PostedBySource,
                dkpAmount = window.DkpAmount,
                isClosingWindow = window.IsClosingWindow,
                isKillWindow = window.IsKillWindow,
                attendees = window.Attendees.Select(attendee => new
                {
                    characterName = attendee.CharacterName,
                    mainCharacterName = attendee.MainCharacterName,
                    zone = attendee.Zone,
                    verifiedAt = attendee.VerifiedAt
                })
            })
        });
    }

    [HttpPost("event-history/{id:int}/edit")]
    public async Task<IActionResult> EditEventHistoryAsync(int id, [FromBody] ActivityEditEventHistoryRequest request, CancellationToken cancellationToken)
    {
        var (history, forbid) = await AuthorizeHistoryEditAsync(id, cancellationToken);
        if (forbid is not null) return forbid;

        await EventHistoryEdits().EditEventAsync(history!.Id,
            new EventHistoryEditInput(request.EventName, request.EventType, request.EventLocation,
                request.Details, request.Duration, request.DkpPerHour),
            cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("event-history/{id:int}/participants/{participantId:int}/dkp")]
    public async Task<IActionResult> SetEventHistoryParticipantDkpAsync(
        int id, int participantId, [FromBody] ActivitySetParticipantDkpRequest request, CancellationToken cancellationToken)
    {
        var (history, forbid) = await AuthorizeHistoryEditAsync(id, cancellationToken);
        if (forbid is not null) return forbid;

        var ok = await EventHistoryEdits()
            .SetParticipantDkpAsync(history!.Id, participantId, request.Amount, cancellationToken);
        return ok ? Ok(new { success = true }) : NotFound(new { error = "Attendee not found." });
    }

    [HttpPost("event-history/{id:int}/participants/{participantId:int}/remove")]
    public async Task<IActionResult> RemoveEventHistoryParticipantAsync(
        int id, int participantId, CancellationToken cancellationToken)
    {
        var (history, forbid) = await AuthorizeHistoryEditAsync(id, cancellationToken);
        if (forbid is not null) return forbid;

        var ok = await EventHistoryEdits()
            .RemoveParticipantAsync(history!.Id, participantId, cancellationToken);
        return ok ? Ok(new { success = true }) : NotFound(new { error = "Attendee not found." });
    }

    [HttpPost("event-history/{id:int}/participants/{participantId:int}/active-credit")]
    public async Task<IActionResult> SetEventHistoryParticipantActiveCreditAsync(
        int id, int participantId, [FromBody] ActivitySetActiveCreditRequest request, CancellationToken cancellationToken)
    {
        var (history, forbid) = await AuthorizeHistoryEditAsync(id, cancellationToken);
        if (forbid is not null) return forbid;

        var ok = await EventHistoryEdits()
            .SetParticipantActiveCreditAsync(history!.Id, participantId, request.Credited, cancellationToken);
        return ok ? Ok(new { success = true }) : NotFound(new { error = "Attendee not found." });
    }

    // Add a member to a closed event after the fact and grant DKP (creates the attendance
    // row + canonical EventEarned ledger entry + adds to balance). Leader/officer only.
    [HttpPost("event-history/{id:int}/participants/add")]
    public async Task<IActionResult> AddEventHistoryParticipantAsync(
        int id, [FromBody] ActivityAddEventHistoryParticipantRequest request, CancellationToken cancellationToken)
    {
        var (history, forbid) = await AuthorizeHistoryEditAsync(id, cancellationToken);
        if (forbid is not null) return forbid;
        if (string.IsNullOrWhiteSpace(request.AppUserId))
        {
            return BadRequest(new { error = "Select a member to add." });
        }

        var ok = await EventHistoryEdits().AddParticipantAsync(
            history!.Id, request.AppUserId, request.Dkp, request.JobType, request.JobName, request.SubJobName,
            activeCredit: true, cancellationToken);
        return ok
            ? Ok(new { success = true })
            : BadRequest(new { error = "Couldn't add that member (already on the event, or not a member of the linkshell)." });
    }

    // Undo active-status credit for the ENTIRE event (every attendee) — for events
    // credited by accident. Recomputes member statuses after.
    [HttpPost("event-history/{id:int}/active-credit/clear")]
    public async Task<IActionResult> ClearEventHistoryActiveCreditAsync(int id, CancellationToken cancellationToken)
    {
        var (history, forbid) = await AuthorizeHistoryEditAsync(id, cancellationToken);
        if (forbid is not null) return forbid;

        var changed = await EventHistoryEdits()
            .SetAllParticipantsActiveCreditAsync(history!.Id, credited: false, cancellationToken);
        return Ok(new { success = true, changed });
    }

    // Undo absences for the ENTIRE event — stop it counting toward active tracking
    // so members who missed it aren't marked absent for it. Recomputes statuses.
    [HttpPost("event-history/{id:int}/absences/clear")]
    public async Task<IActionResult> ClearEventHistoryAbsencesAsync(int id, CancellationToken cancellationToken)
    {
        var (history, forbid) = await AuthorizeHistoryEditAsync(id, cancellationToken);
        if (forbid is not null) return forbid;

        var changed = await EventHistoryEdits()
            .SetEventCountsTowardActiveAsync(history!.Id, counts: false, cancellationToken);
        return Ok(new { success = true, changed });
    }

    // Delete a closed event entirely (reverses its DKP — earned + loot spent — and
    // removes its attendance/loot/comments). Leader/officer only (CanManageEvents).
    [HttpPost("event-history/{id:int}/delete")]
    public async Task<IActionResult> DeleteEventHistoryAsync(int id, CancellationToken cancellationToken)
    {
        var (history, forbid) = await AuthorizeHistoryEditAsync(id, cancellationToken);
        if (forbid is not null) return forbid;

        var ok = await EventHistoryEdits().DeleteEventAsync(history!.Id, cancellationToken);
        return ok ? Ok(new { success = true }) : NotFound(new { error = "Event not found." });
    }

    private async Task<(EventHistory? History, IActionResult? Result)> AuthorizeHistoryEditAsync(int id, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return (null, Unauthorized(new { error = "Sign in to edit event history." }));
        }

        var history = await _dbContext.EventHistories.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (history is null)
        {
            return (null, NotFound(new { error = "Event not found." }));
        }

        var membership = await GetMembershipAsync(appUser.Id, history.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return (null, Forbid());
        }
        return (history, null);
    }
}
