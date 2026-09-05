using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Creates the queued "next repop" event for a freshly-posted HNM ToD.
// Triggered from AddonApiController.PostTodAsync (direct addon post) and
// SubmissionApprovalService.ApproveTodAsync (officer approves a pending
// member ToD). Web/activity ToD posts intentionally do NOT trigger this --
// auto-event creation is addon-only by product design.
public sealed class HnmAutoEventService
{
    private const string AutoEventDetails = "Auto-created from ToD post";

    private readonly ApplicationDbContext _db;
    private readonly ILogger<HnmAutoEventService> _logger;

    public HnmAutoEventService(ApplicationDbContext db, ILogger<HnmAutoEventService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int?> CreateAutoEventForTodAsync(int todId, CancellationToken cancellationToken)
    {
        var tod = await _db.Tods
            .Include(t => t.Linkshell)
            .FirstOrDefaultAsync(t => t.Id == todId, cancellationToken);
        if (tod is null)
        {
            _logger.LogDebug("HNM auto-event skip: tod {TodId} not found.", todId);
            return null;
        }
        if (tod.Linkshell is null || !tod.Linkshell.EnableHnmSection)
        {
            _logger.LogDebug("HNM auto-event skip: linkshell HNM section disabled (tod {TodId}).", todId);
            return null;
        }
        if (!HnmConfig.IsTrueHnm(tod.MonsterName))
        {
            _logger.LogDebug("HNM auto-event skip: monster '{Monster}' is not a tracked HNM (tod {TodId}).",
                tod.MonsterName, todId);
            return null;
        }
        if (!tod.RepopTime.HasValue)
        {
            _logger.LogWarning("HNM auto-event skip: tod {TodId} has no RepopTime.", todId);
            return null;
        }

        // This event is for the NEXT pop, so it carries the next day of the cycle — day N
        // becomes N+1, and an HQ kill spends the cycle and restarts at day 1 on the NQ half
        // (killing Nidhogg means the next spawn is Fafnir). Using the ToD's own day here
        // labelled every auto-event with the day that had just died.
        var wasHq = tod.Hq;
        var monster = (wasHq ? HnmConfig.BaseMonsterName(tod.MonsterName) : tod.MonsterName)!.Trim();
        var nextDay = HnmConfig.NextDayNumber(tod.DayNumber, wasHq);
        var eventName = ComposeEventName(monster, nextDay);
        var repopUtc = tod.RepopTime.Value;

        // Idempotency: an existing queued/live event in this LS for the same
        // composed name within +-10 minutes of the repop time means we've
        // already auto-created (or the officer manually created the equivalent).
        // Link it back to the source ToD and bail without inserting a duplicate.
        var idempotencyWindowStart = repopUtc.AddMinutes(-10);
        var idempotencyWindowEnd = repopUtc.AddMinutes(10);
        var existing = await _db.Events
            .FirstOrDefaultAsync(e => e.LinkshellId == tod.LinkshellId
                                      && e.EventName == eventName
                                      && e.EndTime == null
                                      && e.StartTime != null
                                      && e.StartTime >= idempotencyWindowStart
                                      && e.StartTime <= idempotencyWindowEnd,
                cancellationToken);
        if (existing is not null)
        {
            if (existing.SourceTodId != todId)
            {
                existing.SourceTodId = todId;
                await _db.SaveChangesAsync(cancellationToken);
            }
            _logger.LogDebug("HNM auto-event reuse: tod {TodId} linked to existing event {EventId}.",
                todId, existing.Id);
            return existing.Id;
        }

        // Every spelling of the spawn. MonsterMatchNames is symmetric across the merge pair, so a
        // Fafnir ToD finds the event assigned to Nidhogg and vice versa.
        var monsterNames = HnmConfig.MonsterMatchNamesLower(monster);

        // A LIVE camp for this monster is ALREADY the next pop's board, so stand down rather than
        // queue a second one beside it. Ending a camp recycles its row onto the ToD that was
        // settled for the kill (HnmCampReviewHandoffService.HandOffAndRecycleAsync) instead of
        // deleting the row the way it once did, and the addon posts that ToD in a separate call
        // BEFORE the end — from the End Event dialog, or from the ToD Capture panel earlier in the
        // night. So the camp is still live when we get here, which is exactly why the recycle below
        // misses it, and one kill was leaving the officer with two queued events: the parked camp
        // and this auto-event.
        //
        // Not folded into that recycle: skipping a live camp there protects a payout still being
        // accrued (see below), so the answer here has to be "stand down", not "revive it".
        var liveCamp = await _db.Events
            .Where(e => e.LinkshellId == tod.LinkshellId
                        && e.EndTime == null
                        && e.CommencementStartTime != null
                        && e.AssignedMonsterName != null
                        && monsterNames.Contains(e.AssignedMonsterName.ToLower()))
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (liveCamp is { } liveCampId)
        {
            _logger.LogDebug(
                "HNM auto-event skip: camp {EventId} ('{Monster}') is still live; ending it recycles that board onto tod {TodId}.",
                liveCampId, monster, todId);
            return null;
        }

        // Nothing matched the repop window, so this monster's LAST pop is what's still on the board.
        // Recycle that row for the new pop rather than stacking a second event beside it — otherwise
        // every kill leaves another entry in Queued Events and the old camps pile up until somebody
        // ends them by hand.
        //
        // Deliberately QUEUED ONLY (CommencementStartTime == null). A live camp has attendance and
        // DKP accruing against it, and reviving it would wipe a night people are still being paid
        // for. The guard above turns those away entirely; this one is the difference between
        // recycling a row and destroying a payout.
        //
        // Matched on AssignedMonsterName, not EventName: the name carries a "D<n>" suffix that
        // changes every pop, which is exactly why the ±10-minute check above misses.
        var stale = await _db.Events
            .Where(e => e.LinkshellId == tod.LinkshellId
                        && e.EndTime == null
                        && e.CommencementStartTime == null
                        && e.AssignedMonsterName != null
                        && monsterNames.Contains(e.AssignedMonsterName.ToLower()))
            // Newest first, and only ever one: two rows for one monster is already a mistake, and
            // rewriting both from a single ToD would compound it.
            .OrderByDescending(e => e.StartTime)
            .ThenByDescending(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (stale is not null)
        {
            stale.EventName = eventName;
            await HnmEventSeeder.ReviveForNewPopAsync(
                _db, stale, repopUtc, nextDay, monster, todId, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "HNM auto-event revived: event {EventId} ({EventName}) re-queued from tod {TodId}, repop {Repop:o}.",
                stale.Id, eventName, todId, repopUtc);
            return stale.Id;
        }

        var priorDkpPerHour = await GetPriorDkpPerHourAsync(tod.LinkshellId, eventName, monster, cancellationToken);

        var creatorUserId = await ResolveCreatorUserIdAsync(tod, cancellationToken);

        var autoEvent = new Event
        {
            LinkshellId = tod.LinkshellId,
            EventName = eventName,
            EventType = "HNM",
            EventLocation = HnmConfig.HnmZones.TryGetValue(monster, out var zone) ? zone : null,
            CreatorUserId = creatorUserId,
            StartTime = repopUtc,
            CommencementStartTime = null,
            DkpPerHour = priorDkpPerHour,
            Details = AutoEventDetails,
            CreationSource = "Addon",
            SourceTodId = todId,
            // Every other board creator stamps this; without it the auto-event has no monster,
            // so the board can't render the NQ/HQ name for its day and End Camp rejects it
            // ("no monster assigned"). It's what makes the day cycle above mean anything.
            AssignedMonsterName = monster,
            DayNumber = nextDay,
            // Inherit the board from the camp this replaces, the same way DkpPerHour above is
            // inherited. Ending a camp DELETES its Event row, so without this a camp ended from
            // the addon and re-created from its own ToD came back with no board attached and an
            // officer had to re-pick it every pop.
            PartySetupId = await PartySetupInheritance.ResolveForNewPopAsync(
                _db, tod.LinkshellId, monster, eventName, cancellationToken),
            TimeStamp = DateTime.UtcNow,
        };
        // Seed the built-in per-monster window count + Manual Check In stamp.
        await HnmEventSeeder.SeedHnmEventAsync(_db, autoEvent, tod.Linkshell, monster, cancellationToken);
        _db.Events.Add(autoEvent);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("HNM auto-event created: event {EventId} ({EventName}) from tod {TodId}, repop {Repop:o}.",
            autoEvent.Id, eventName, todId, repopUtc);
        return autoEvent.Id;
    }

    private static string ComposeEventName(string monster, int? dayNumber)
    {
        if (HnmConfig.MonsterSegments(monster).Any(HnmConfig.HnmDayCycles.ContainsKey) && dayNumber.HasValue && dayNumber.Value > 0)
        {
            return $"{monster} D{dayNumber.Value}";
        }
        return monster;
    }

    // Look up the most recent same-name event in this linkshell (active table
    // first; falls back to EventHistory) and copy its DKP/hr. Returns null
    // when no prior event exists -- officer can fill the value in before
    // starting the auto-event. We also try the bare monster name in case the
    // prior event was a different day (e.g. last week was Nidhogg D2 but this
    // week is Nidhogg D3 -- still a reasonable DKP carry-over).
    private async Task<int?> GetPriorDkpPerHourAsync(int linkshellId, string eventName, string monster, CancellationToken ct)
    {
        var fromActive = await _db.Events
            .Where(e => e.LinkshellId == linkshellId && e.EventName == eventName && e.DkpPerHour != null)
            .OrderByDescending(e => e.TimeStamp)
            .Select(e => e.DkpPerHour)
            .FirstOrDefaultAsync(ct);
        if (fromActive.HasValue) return fromActive;

        var fromHistory = await _db.EventHistories
            .Where(e => e.LinkshellId == linkshellId && e.EventName == eventName && e.DkpPerHour != null)
            .OrderByDescending(e => e.TimeStamp)
            .Select(e => e.DkpPerHour)
            .FirstOrDefaultAsync(ct);
        if (fromHistory.HasValue) return fromHistory;

        if (!string.Equals(eventName, monster, StringComparison.Ordinal))
        {
            var fromActiveBare = await _db.Events
                .Where(e => e.LinkshellId == linkshellId && e.EventName == monster && e.DkpPerHour != null)
                .OrderByDescending(e => e.TimeStamp)
                .Select(e => e.DkpPerHour)
                .FirstOrDefaultAsync(ct);
            if (fromActiveBare.HasValue) return fromActiveBare;

            var fromHistoryBare = await _db.EventHistories
                .Where(e => e.LinkshellId == linkshellId && e.EventName == monster && e.DkpPerHour != null)
                .OrderByDescending(e => e.TimeStamp)
                .Select(e => e.DkpPerHour)
                .FirstOrDefaultAsync(ct);
            if (fromHistoryBare.HasValue) return fromHistoryBare;
        }

        return null;
    }

    // The ToD model doesn't track its creator directly. Fall back to the
    // linkshell's owner AppUserId so CreatorUserId is at least populated --
    // downstream UI uses this to render the "Created by" pill on the event.
    private async Task<string?> ResolveCreatorUserIdAsync(Tod tod, CancellationToken ct)
    {
        if (tod.Linkshell is not null && !string.IsNullOrEmpty(tod.Linkshell.AppUserId))
        {
            return tod.Linkshell.AppUserId;
        }
        return await _db.Linkshells
            .Where(l => l.Id == tod.LinkshellId)
            .Select(l => l.AppUserId)
            .FirstOrDefaultAsync(ct);
    }
}
