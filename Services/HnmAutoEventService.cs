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

        var monster = tod.MonsterName!.Trim();
        var eventName = ComposeEventName(monster, tod.DayNumber);
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
            WindowCountOverride = HnmConfig.GetWindowCount(monster),
            SourceTodId = todId,
            TimeStamp = DateTime.UtcNow,
        };
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
