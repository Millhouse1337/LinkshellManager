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

        var histories = await _dbContext.EventHistories
            .AsNoTracking()
            .Include(h => h.AppUserEventHistories)
            .Where(h => h.LinkshellId == linkshellId)
            .OrderByDescending(h => h.EndTime ?? h.TimeStamp)
            .Take(100)
            .ToListAsync(cancellationToken);

        var result = histories.Select(h => new
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
            participants = h.AppUserEventHistories
                .OrderBy(p => p.CharacterName)
                .Select(p => new
                {
                    id = p.Id,
                    characterName = p.CharacterName,
                    jobName = p.JobName,
                    subJobName = p.SubJobName,
                    duration = p.Duration,
                    eventDkp = p.EventDkp,
                    activeCredit = p.ActiveCredit
                })
        });

        return Ok(new { canManage, histories = result });
    }

    [HttpPost("event-history/{id:int}/edit")]
    public async Task<IActionResult> EditEventHistoryAsync(int id, [FromBody] ActivityEditEventHistoryRequest request, CancellationToken cancellationToken)
    {
        var (history, forbid) = await AuthorizeHistoryEditAsync(id, cancellationToken);
        if (forbid is not null) return forbid;

        await new EventHistoryEditService(_dbContext).EditEventAsync(history!.Id,
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

        var ok = await new EventHistoryEditService(_dbContext)
            .SetParticipantDkpAsync(history!.Id, participantId, request.Amount, cancellationToken);
        return ok ? Ok(new { success = true }) : NotFound(new { error = "Attendee not found." });
    }

    [HttpPost("event-history/{id:int}/participants/{participantId:int}/remove")]
    public async Task<IActionResult> RemoveEventHistoryParticipantAsync(
        int id, int participantId, CancellationToken cancellationToken)
    {
        var (history, forbid) = await AuthorizeHistoryEditAsync(id, cancellationToken);
        if (forbid is not null) return forbid;

        var ok = await new EventHistoryEditService(_dbContext)
            .RemoveParticipantAsync(history!.Id, participantId, cancellationToken);
        return ok ? Ok(new { success = true }) : NotFound(new { error = "Attendee not found." });
    }

    [HttpPost("event-history/{id:int}/participants/{participantId:int}/active-credit")]
    public async Task<IActionResult> SetEventHistoryParticipantActiveCreditAsync(
        int id, int participantId, [FromBody] ActivitySetActiveCreditRequest request, CancellationToken cancellationToken)
    {
        var (history, forbid) = await AuthorizeHistoryEditAsync(id, cancellationToken);
        if (forbid is not null) return forbid;

        var ok = await new EventHistoryEditService(_dbContext)
            .SetParticipantActiveCreditAsync(history!.Id, participantId, request.Credited, cancellationToken);
        return ok ? Ok(new { success = true }) : NotFound(new { error = "Attendee not found." });
    }

    // Undo active-status credit for the ENTIRE event (every attendee) — for events
    // credited by accident. Recomputes member statuses after.
    [HttpPost("event-history/{id:int}/active-credit/clear")]
    public async Task<IActionResult> ClearEventHistoryActiveCreditAsync(int id, CancellationToken cancellationToken)
    {
        var (history, forbid) = await AuthorizeHistoryEditAsync(id, cancellationToken);
        if (forbid is not null) return forbid;

        var changed = await new EventHistoryEditService(_dbContext)
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

        var changed = await new EventHistoryEditService(_dbContext)
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

        var ok = await new EventHistoryEditService(_dbContext).DeleteEventAsync(history!.Id, cancellationToken);
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
