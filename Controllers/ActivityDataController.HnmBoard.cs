using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    // Logs (or edits) the monster's Time of Death from an HNM signup board's "Post ToD" /
    // "Edit ToD" button. It records the ToD (which drives the recurring-board re-post),
    // moves the event's StartTime to the predicted repop, wipes the board's signups, marks
    // it "defeated / awaiting re-post", and replaces the Discord board message with a
    // defeated note. The HnmRecurringBoardBackgroundService later clears the flag and
    // re-posts THIS same board LeadHours before the pop (one card that cycles).
    [HttpPost("events/{eventId:int}/post-tod")]
    public async Task<IActionResult> PostBoardTodAsync(
        int eventId,
        [FromBody] ActivityPostBoardTodRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to log a Time of Death." });
        }

        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageEvents, cancellationToken))
        {
            return Forbid();
        }

        var isHnm = string.Equals((eventEntity.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        if (!isHnm)
        {
            return BadRequest(new { error = "Time of Death can only be posted from an HNM signup board." });
        }

        var monsterName = eventEntity.AssignedMonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            return BadRequest(new { error = "This HNM board has no monster assigned." });
        }

        if (!TryConvertUserTimeZoneToUtc(request.TimeLocal, appUser.TimeZone, out var todTimeUtc) || !todTimeUtc.HasValue)
        {
            return BadRequest(new { error = "Enter a valid Time of Death using your local time." });
        }

        var cooldown = string.IsNullOrWhiteSpace(request.Cooldown)
            ? GetDefaultTodCooldown(monsterName)
            : request.Cooldown.Trim();
        if (!IsAcceptableTodCooldown(cooldown))
        {
            return BadRequest(new { error = "Enter a valid cooldown (e.g. 22 Hour, 72 Hour, or a positive number of hours)." });
        }

        var interval = request.Interval?.Trim();
        if (string.IsNullOrWhiteSpace(interval))
        {
            interval = null;
        }
        else if (!SupportedTodIntervals.Contains(interval))
        {
            return BadRequest(new { error = "Select a valid interval." });
        }

        var nowUtc = DateTime.UtcNow;
        var repopUtc = todTimeUtc.Value.AddHours(ResolveTodCooldownHours(cooldown));

        // Edit the existing ToD if we're already in the defeated/awaiting state (the card's
        // "Edit ToD" button); otherwise log a fresh ToD for this pop ("Post ToD").
        Tod? tod = null;
        if (eventEntity.HnmDefeatedAt != null && eventEntity.SourceTodId is { } sourceTodId)
        {
            tod = await _dbContext.Tods.FirstOrDefaultAsync(t => t.Id == sourceTodId, cancellationToken);
        }
        if (tod is null)
        {
            tod = new Tod { LinkshellId = eventEntity.LinkshellId, TotalTods = 1 };
            _dbContext.Tods.Add(tod);
        }
        tod.MonsterName = monsterName;
        tod.DayNumber = request.DayNumber;
        tod.Claim = request.Claim;
        tod.Time = todTimeUtc;
        tod.Cooldown = cooldown;
        tod.RepopTime = repopUtc;
        tod.Interval = interval;
        tod.TimeStamp = nowUtc;
        tod.TotalClaims = request.Claim == true ? 1 : 0;
        await _dbContext.SaveChangesAsync(cancellationToken); // ensure tod.Id is assigned

        // Re-point the event to this pop's repop time + ToD, and mark the board defeated.
        eventEntity.StartTime = repopUtc;
        eventEntity.SourceTodId = tod.Id;
        eventEntity.HnmDefeatedAt = nowUtc;

        // The board auto-re-posts LeadHours before the pop — but only if Repeat-on-ToD is on
        // (an enabled recurring board exists). Otherwise there's no auto-re-post window.
        var leadHours = await _dbContext.HnmRecurringBoards
            .Where(b => b.LinkshellId == eventEntity.LinkshellId
                && b.MonsterName.ToLower() == monsterName.ToLower()
                && b.Enabled)
            .Select(b => (double?)b.LeadHours)
            .FirstOrDefaultAsync(cancellationToken);
        eventEntity.HnmRepostAt = leadHours.HasValue ? repopUtc.AddHours(-leadHours.Value) : null;

        // Wipe the board's signups — the pop is done. (Both the party-slot signups and the
        // no-slot attendees.) Deliberately do NOT stamp the recurring board's LastSourceTodId
        // here: leaving it lets the poller find this defeated event at the lead window and
        // re-post THIS same board instead of creating a new one.
        var slotSignups = await _dbContext.EventPartySlotSignups
            .Where(s => s.EventId == eventId)
            .ToListAsync(cancellationToken);
        _dbContext.EventPartySlotSignups.RemoveRange(slotSignups);
        var noSlotAttendees = await _dbContext.AppUserEvents
            .Where(p => p.EventId == eventId)
            .ToListAsync(cancellationToken);
        _dbContext.AppUserEvents.RemoveRange(noSlotAttendees);

        // The Event is now Modified with a non-null HnmDefeatedAt, so the DbContext
        // save-hook enqueues it and DiscordEventChannelPublisher edits the posted board
        // message into the "defeated" note (single renderer → no race with this save).
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            success = true,
            repopTimeUtc = repopUtc,
            repostAtUtc = eventEntity.HnmRepostAt
        });
    }
}
