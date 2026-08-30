using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Re-posts a standing HNM signup board (HnmRecurringBoard) before the next predicted
// pop. Each tick, for every enabled template, it finds the monster's latest ToD with
// a RepopTime; once now is within LeadHours of that pop it creates a queued HNM event
// (which the DbContext save-hook auto-posts to Discord) and advances LastSourceTodId
// so each ToD fires at most once. Mirrors EventAutoStartBackgroundService.
//
// Coexistence with the addon auto-event path (HnmAutoEventService): the idempotency
// check matches on SourceTodId OR same-name-within-±10min, so on addon linkshells the
// poller reuses the event the addon already created (no duplicate) and just stamps the
// template. On web/Activity-only linkshells the poller is the sole creator — and it
// does NOT require EnableHnmSection / IsTrueHnm, so it works for the full signup-board
// monster list, gated instead by the template's existence + Enabled flag.
public sealed class HnmRecurringBoardBackgroundService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    // Don't post a board for a pop that's already long past (dormant template, or the
    // app was down across the window). Past this we skip but still advance the stamp.
    // Public because HnmRecurringBoardService.SyncParkedBoardsForTodAsync has to answer the
    // same "is this pop still worth posting for?" question when a corrected ToD un-sticks a
    // cycle this loop gave up on — the two must agree or a ToD could be revived here and
    // abandoned again on the next tick.
    public static readonly TimeSpan PostGrace = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HnmRecurringBoardBackgroundService> _logger;

    public HnmRecurringBoardBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<HnmRecurringBoardBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueBoardsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in HnmRecurringBoardBackgroundService loop.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessDueBoardsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var boards = await db.HnmRecurringBoards
            .Where(board => board.Enabled)
            .ToListAsync(cancellationToken);
        if (boards.Count == 0)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        foreach (var board in boards)
        {
            try
            {
                await ProcessBoardAsync(db, board, nowUtc, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing HNM recurring board {BoardId} (monster '{Monster}').",
                    board.Id, board.MonsterName);
            }
        }
    }

    private async Task ProcessBoardAsync(
        ApplicationDbContext db, HnmRecurringBoard board, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var monster = board.MonsterName.Trim();
        // A board may be keyed on a combined "Base/Stronger" name, and the ToD logged from
        // that board records the same combined name — so match on every spelling of the
        // spawn (the combined label AND either half), not just the segments.
        var names = HnmConfig.MonsterMatchNamesLower(monster);

        // Latest ToD for this monster that has a predicted pop time.
        var tod = names.Count == 0 ? null : await db.Tods
            .Where(t => t.LinkshellId == board.LinkshellId
                && t.MonsterName != null
                && names.Contains(t.MonsterName.ToLower())
                && t.RepopTime != null)
            .OrderByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (tod is null || tod.Id == board.LastSourceTodId)
        {
            return; // no ToD yet, or this pop cycle was already handled
        }

        var repopUtc = tod.RepopTime!.Value;
        var postAt = repopUtc.AddHours(-board.LeadHours);
        if (nowUtc < postAt)
        {
            return; // not yet inside the lead window; re-evaluate next tick
        }
        if (nowUtc > repopUtc.Add(PostGrace))
        {
            // Pop already well past — don't post a stale board, but advance the stamp
            // so we don't keep re-evaluating this ToD every tick.
            board.LastSourceTodId = tod.Id;
            board.UpdatedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var eventName = string.IsNullOrWhiteSpace(board.EventNameTemplate) ? monster : board.EventNameTemplate!;
        var windowStart = repopUtc.AddMinutes(-10);
        var windowEnd = repopUtc.AddMinutes(10);

        // The re-posted board is for the NEXT pop, so it advances the day cycle: day N becomes
        // N+1, and an HQ kill resets to day 1. On a combined "Base/Stronger" board day 1 already
        // renders as the base half; a board keyed on the bare stronger name needs the swap
        // spelled out, since "Nidhogg" killed means the next spawn is Fafnir. The TEMPLATE's
        // MonsterName is left alone — it's the stable key for this spawn's standing board.
        var nextDay = HnmConfig.NextDayNumber(tod.DayNumber, tod.Hq);
        var nextMonster = tod.Hq ? HnmConfig.BaseMonsterName(monster)! : monster;

        // Idempotency / addon coexistence: reuse an existing not-yet-ended event that
        // was created from this exact ToD, or a same-named event within ±10 min of the
        // pop (covers the addon's auto-event, whose composed name may differ).
        var existing = await db.Events.FirstOrDefaultAsync(e =>
            e.LinkshellId == board.LinkshellId
            && e.EndTime == null
            && (e.SourceTodId == tod.Id
                || (e.EventName == eventName
                    && e.StartTime != null
                    && e.StartTime >= windowStart
                    && e.StartTime <= windowEnd)),
            cancellationToken);

        if (existing is null)
        {
            var creatorUserId = await db.Linkshells
                .Where(l => l.Id == board.LinkshellId)
                .Select(l => l.AppUserId)
                .FirstOrDefaultAsync(cancellationToken);

            var newEvent = new Event
            {
                LinkshellId = board.LinkshellId,
                EventName = eventName,
                EventType = "HNM",
                EventLocation = string.IsNullOrWhiteSpace(board.EventLocation)
                    ? HnmConfig.ZoneFor(monster)
                    : board.EventLocation,
                AssignedMonsterName = nextMonster,
                CreatorUserId = creatorUserId ?? board.CreatedByAppUserId,
                StartTime = repopUtc,
                CommencementStartTime = null,
                DkpPerHour = 0,
                CountsTowardActive = false,
                AutoStart = false,
                Details = board.Details,
                PartySetupId = board.PartySetupId,
                SourceTodId = tod.Id,
                DayNumber = nextDay,
                TimeStamp = nowUtc,
            };
            // Seed window count (per-linkshell override or HnmConfig fallback) + Manual Check In stamp.
            await HnmEventSeeder.SeedHnmEventAsync(db, newEvent, null, monster, cancellationToken);
            db.Events.Add(newEvent);
            _logger.LogInformation(
                "HNM recurring board posted: event '{EventName}' (linkshell {LinkshellId}, monster '{Monster}') from tod {TodId}, repop {Repop:o}, lead {Lead}h.",
                eventName, board.LinkshellId, monster, tod.Id, repopUtc, board.LeadHours);
        }
        else
        {
            if (existing.SourceTodId != tod.Id)
            {
                existing.SourceTodId = tod.Id;
            }
            // A manually-posted ToD parks the board in a "defeated / awaiting re-post" state
            // (signups wiped, StartTime = repop, Discord message replaced with a note). Now
            // that we're inside the lead window, bring THAT same board back to life instead
            // of creating a new event: clearing the flag re-renders a fresh, empty board.
            // The DbContext save-hook re-posts it to Discord (it edits the existing message,
            // since the event keeps its message id), the same way new boards are posted.
            //
            // The reset itself is shared with HnmAutoEventService, so a board revived by the
            // addon's ToD post and one revived here come out identical.
            if (existing.HnmDefeatedAt != null)
            {
                await HnmEventSeeder.ReviveForNewPopAsync(
                    db, existing, repopUtc, nextDay, nextMonster, tod.Id, cancellationToken);
                _logger.LogInformation(
                    "HNM board reactivated: event '{EventName}' ({EventId}, linkshell {LinkshellId}, monster '{Monster}') from tod {TodId}, repop {Repop:o}.",
                    existing.EventName, existing.Id, board.LinkshellId, monster, tod.Id, repopUtc);
            }
        }

        board.LastSourceTodId = tod.Id;
        board.UpdatedAt = nowUtc;
        await db.SaveChangesAsync(cancellationToken);
    }
}
