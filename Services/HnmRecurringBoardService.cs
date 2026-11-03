using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// The standing "repost the HNM event before the next pop" template for a monster. Switched on and
// off only from the End Camp / Post ToD form (ApplyEndCampChoiceAsync), kept in step with ToD
// edits, and switched off when its board is removed. The poller
// (HnmRecurringBoardBackgroundService) reads these rows and re-posts the board before the next
// predicted pop.
public static class HnmRecurringBoardService
{
    // Default lead when a board is switched on and no lead has ever been set for it.
    public const double DefaultLeadHours = 1;

    // Apply the End Camp / Post ToD form's re-post choice: enable or disable this monster's
    // standing board and set its lead. Enabling seeds the template from THIS event (name,
    // party setup, location, details) so the re-posted board matches; disabling flips Enabled
    // off but keeps the row and its LastSourceTodId stamp.
    //
    // This deliberately does NOT re-stamp LastSourceTodId. End Camp has just logged the ToD that
    // the poller must act on — stamping it here would mark the cycle handled and the board would
    // never re-post, which is the whole point of the form. Self-committing.
    public static async Task ApplyEndCampChoiceAsync(
        ApplicationDbContext db, Event ev, string monster, bool enabled, double? leadHours,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var board = await FindAsync(db, ev.LinkshellId, monster, cancellationToken);

        if (board is null)
        {
            if (!enabled)
            {
                return; // nothing to disable — no standing board exists
            }
            board = new HnmRecurringBoard
            {
                LinkshellId = ev.LinkshellId,
                MonsterName = monster,
                CreatedByAppUserId = ev.CreatorUserId,
                CreatedAt = nowUtc,
            };
            db.HnmRecurringBoards.Add(board);
        }

        board.Enabled = enabled;
        if (enabled)
        {
            // Positive lead only; fall back to the existing/default lead when none was entered.
            if (leadHours is { } lead && lead > 0)
            {
                board.LeadHours = lead;
            }
            else if (board.LeadHours <= 0)
            {
                board.LeadHours = DefaultLeadHours;
            }
            // Refresh the template from the current event so a re-post reproduces this board.
            // PartySetupId resolves to a TEMPLATE — see the note on the other assignment above.
            board.EventNameTemplate = ev.EventName;
            board.PartySetupId = await PartySetupInheritance.ResolveTemplateIdAsync(db, ev, cancellationToken);
            board.EventLocation = ev.EventLocation;
            board.Details = ev.Details;
        }
        board.UpdatedAt = nowUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    // Re-derive a defeated board's DISPLAYED re-post time (Event.HnmRepostAt) from the
    // monster's current standing lead. Call it after an event edit, which may have moved the
    // board or switched its monster: HnmRecurringBoardBackgroundService always recomputes its own
    // window from board.LeadHours, so the re-post already happens at the new time — without
    // this the card would just keep advertising the old one. No enabled board = null = "this
    // board won't auto-re-post", matching the End Camp paths. Self-committing.
    public static async Task RefreshRepostAtAsync(
        ApplicationDbContext db, Event ev, string? monsterName, CancellationToken cancellationToken)
    {
        // Only a defeated board is waiting on a re-post; a live one has nothing scheduled.
        var monster = monsterName?.Trim();
        if (ev.HnmDefeatedAt is null || string.IsNullOrWhiteSpace(monster))
        {
            return;
        }

        // Matched on every spelling of the spawn, the same way the poller finds the board.
        var names = HnmConfig.MonsterMatchNamesLower(monster);
        var leadHours = names.Count == 0
            ? null
            : await db.HnmRecurringBoards
                .Where(b => b.LinkshellId == ev.LinkshellId
                    && names.Contains(b.MonsterName.ToLower())
                    && b.Enabled)
                .Select(b => (double?)b.LeadHours)
                .FirstOrDefaultAsync(cancellationToken);

        // The pop being waited on is the source ToD's prediction (StartTime mirrors it, and
        // stands in for the rows that predate SourceTodId).
        var repopUtc = ev.SourceTodId is { } todId
            ? await db.Tods
                .Where(tod => tod.Id == todId)
                .Select(tod => tod.RepopTime)
                .FirstOrDefaultAsync(cancellationToken) ?? ev.StartTime
            : ev.StartTime;

        var refreshed = repopUtc is { } anchor && leadHours.HasValue
            ? anchor.AddHours(-leadHours.Value)
            : (DateTime?)null;
        if (refreshed == ev.HnmRepostAt)
        {
            return;
        }

        ev.HnmRepostAt = refreshed;
        await db.SaveChangesAsync(cancellationToken);
    }

    // Bring a monster's PARKED boards back in step after its ToD changed from the ToD tracker
    // (create, edit or delete), which writes only the Tod row and knows nothing about events.
    // Fixes two ways a corrected ToD looked ignored:
    //
    //   1. Stale display. A parked board's StartTime (its predicted repop) and HnmRepostAt are
    //      stamped at End Camp, not derived. HnmRecurringBoardBackgroundService recomputes the
    //      REAL post time from the ToD every tick, so the board already moved with the ToD —
    //      the card just kept advertising the old times until it did.
    //   2. A stuck cycle. If the repop drifted more than PostGrace into the past, the poller
    //      gave up on that ToD and stamped LastSourceTodId. Correcting that same ToD afterwards
    //      could never fire, because the stamp still matched. Clearing it re-opens the cycle.
    //
    // Only PARKED boards (HnmDefeatedAt set) are touched, and clearing the stamp is gated on one
    // existing — a board that already re-posted has HnmDefeatedAt cleared, so this can't make it
    // post twice. LIVE camps are deliberately left alone: their window grid is anchored to
    // StartTime, so moving it mid-camp would scramble the window counter and the attendance
    // windows. Self-committing; a no-op when nothing is parked.
    public static async Task SyncParkedBoardsForTodAsync(
        ApplicationDbContext db, int linkshellId, string? monsterName, CancellationToken cancellationToken)
    {
        var monster = monsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monster))
        {
            return;
        }

        var names = HnmConfig.MonsterMatchNamesLower(monster);
        if (names.Count == 0)
        {
            return;
        }

        var parked = await db.Events
            .Where(e => e.LinkshellId == linkshellId
                && e.HnmDefeatedAt != null
                && e.AssignedMonsterName != null
                && names.Contains(e.AssignedMonsterName.ToLower()))
            .ToListAsync(cancellationToken);
        if (parked.Count == 0)
        {
            return;
        }

        // Derive from the monster's NEWEST ToD, not necessarily the row that was just touched —
        // that's the one the poller acts on, so it's the only honest source for the display.
        var latest = await db.Tods
            .Where(tod => tod.LinkshellId == linkshellId
                && tod.MonsterName != null
                && names.Contains(tod.MonsterName.ToLower())
                && tod.RepopTime != null)
            .OrderByDescending(tod => tod.Id)
            .Select(tod => new { tod.Id, tod.RepopTime })
            .FirstOrDefaultAsync(cancellationToken);

        var nowUtc = DateTime.UtcNow;
        var board = await FindAsync(db, linkshellId, monster, cancellationToken);
        var changed = false;

        // With every ToD for the monster gone (the last one was deleted) there's no predicted pop
        // left, so the parked boards advertise nothing rather than a time from a deleted row.
        var repopUtc = latest?.RepopTime;

        if (board is not null
            && latest is not null
            && board.LastSourceTodId == latest.Id
            && repopUtc is { } stuckRepop
            && nowUtc <= stuckRepop.Add(HnmRecurringBoardBackgroundService.PostGrace))
        {
            board.LastSourceTodId = null;
            board.UpdatedAt = nowUtc;
            changed = true;
        }

        var leadHours = board?.Enabled == true ? board.LeadHours : (double?)null;
        var repostAt = repopUtc is { } anchor && leadHours.HasValue
            ? anchor.AddHours(-leadHours.Value)
            : (DateTime?)null;

        foreach (var ev in parked)
        {
            // StartTime IS the predicted repop while a board is parked, so it moves with the ToD.
            if (repopUtc.HasValue && ev.StartTime != repopUtc)
            {
                ev.StartTime = repopUtc;
                changed = true;
            }
            if (ev.HnmRepostAt != repostAt)
            {
                ev.HnmRepostAt = repostAt;
                changed = true;
            }
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // Switches the monster's Repeat-on-ToD template OFF when an officer removes its board -- Cancel,
    // or Delete Camp -- unless another open board for the same spawn is still up.
    //
    // The template outlives its event on purpose: it is keyed on the monster, so the next ToD can
    // find it. That is exactly why removing the event used to leave it armed. The next ToD for the
    // monster had the poller post a brand-new board from it, under the removed event's name, party
    // setup and location, with nobody having asked -- a camp that "created itself". Repeat is a
    // setting of the board an officer made; removing that board is them saying it is not coming
    // back.
    //
    // Another open board for the spawn keeps it on: the template is shared per monster, and that
    // board is still counting on it.
    //
    // STAGES the change and does not save. Callers commit it in the same SaveChanges as the delete,
    // so a delete that fails cannot leave the repeat switched off behind it, or the reverse.
    public static async Task StopRepeatingForRemovedBoardAsync(
        ApplicationDbContext db, Event removed, CancellationToken cancellationToken)
    {
        if (!DiscordEventMessageBuilder.IsHnm(removed))
        {
            return;
        }
        var monster = removed.AssignedMonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monster))
        {
            return;
        }

        var board = await FindAsync(db, removed.LinkshellId, monster, cancellationToken);
        if (board is null || !board.Enabled)
        {
            return;
        }

        var names = HnmConfig.MonsterMatchNamesLower(monster);
        var anotherBoardUp = await db.Events.AnyAsync(e =>
                e.LinkshellId == removed.LinkshellId
                && e.Id != removed.Id
                && e.EndTime == null
                && e.AssignedMonsterName != null
                && names.Contains(e.AssignedMonsterName.ToLower()),
            cancellationToken);
        if (anotherBoardUp)
        {
            return;
        }

        board.Enabled = false;
        board.UpdatedAt = DateTime.UtcNow;
    }

    // A board parked "defeated / awaiting re-post" that an officer STARTS by hand -- from the addon
    // or the Activity -- before the poller has brought it back for the next pop.
    //
    // Ending a camp with Repost on keeps its Event row, stamped HnmDefeatedAt and WdFinalizedAt, and
    // the poller revives it LeadHours before the pop. Starting it earlier only set the start time,
    // so the camp was live AND still marked ended: the Activity keeps ended camps out of the live
    // list and started ones out of the queue, and the camp disappeared from both.
    //
    // So the start revives it first, exactly as the poller would for the monster's newest ToD (next
    // pop, day N+1 or day 1 after an HQ, the base monster after an HQ), and stamps the board so the
    // poller treats that ToD as handled. Stages only; the caller saves. False when not parked.
    public static async Task<bool> ReviveParkedBoardForStartAsync(
        ApplicationDbContext db, Event ev, CancellationToken cancellationToken)
    {
        if (ev.HnmDefeatedAt is null && ev.WdFinalizedAt is null)
        {
            return false;
        }

        var monster = ev.AssignedMonsterName?.Trim() ?? string.Empty;
        var board = await FindAsync(db, ev.LinkshellId, monster, cancellationToken);
        var spawn = board?.MonsterName.Trim() ?? monster;
        var names = HnmConfig.MonsterMatchNamesLower(spawn);
        var tod = names.Count == 0 ? null : await db.Tods
            .Where(t => t.LinkshellId == ev.LinkshellId
                && t.MonsterName != null
                && names.Contains(t.MonsterName.ToLower())
                && t.RepopTime != null)
            .OrderByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (tod is null)
        {
            // No predicted pop to move to: clear the ended state and keep what the board shows.
            await HnmEventSeeder.ReviveForNewPopAsync(
                db, ev, ev.StartTime ?? DateTime.UtcNow, ev.DayNumber, ev.AssignedMonsterName, null, cancellationToken);
            return true;
        }

        var nextDay = HnmConfig.NextDayNumber(tod.DayNumber, tod.Hq);
        var nextMonster = tod.Hq ? HnmConfig.BaseMonsterName(spawn) ?? spawn : spawn;
        await HnmEventSeeder.ReviveForNewPopAsync(
            db, ev, tod.RepopTime!.Value, nextDay, nextMonster, tod.Id, cancellationToken);
        if (board is not null)
        {
            board.LastSourceTodId = tod.Id;
            board.UpdatedAt = DateTime.UtcNow;
        }
        return true;
    }

    // The standing board for this SPAWN. Matched on every spelling of the monster (both
    // halves of a merge pair and the combined label) so a board first created under
    // "Fafnir" on day 2 is the same row the day-4 "Fafnir/Nidhogg" event updates. Keying
    // on the exact string instead would leave two enabled rows for one spawn, and both
    // would fire on the same ToD — two boards posted for one pop.
    public static Task<HnmRecurringBoard?> FindAsync(
        ApplicationDbContext db, int linkshellId, string monster, CancellationToken cancellationToken)
    {
        var names = HnmConfig.MonsterMatchNamesLower(monster);
        if (names.Count == 0)
        {
            return Task.FromResult<HnmRecurringBoard?>(null);
        }
        return db.HnmRecurringBoards
            .Where(board => board.LinkshellId == linkshellId && names.Contains(board.MonsterName.ToLower()))
            .OrderBy(board => board.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // The most recent ToD (by row id) for this monster that has a predicted pop time — the same
    // ToD the poller targets, so a camp that adopts it (HnmCampReviewHandoffService) and the
    // poller agree on which pop is next.
    public static Task<int?> LatestTodIdAsync(
        ApplicationDbContext db, int linkshellId, string monster, CancellationToken cancellationToken)
    {
        // A recurring board may be keyed on a combined "Base/Stronger" name (e.g.
        // "Adamantoise/Aspidochelone"). A ToD may be recorded under either half OR under
        // that same combined label (board-posted ToDs copy AssignedMonsterName verbatim),
        // so match every spelling of the spawn. Must stay in step with the poller's lookup
        // — this stamp is what tells it which pop cycle was already handled.
        var names = HnmConfig.MonsterMatchNamesLower(monster);
        if (names.Count == 0)
        {
            return Task.FromResult<int?>(null);
        }
        return db.Tods
            .Where(tod => tod.LinkshellId == linkshellId
                && tod.MonsterName != null
                && names.Contains(tod.MonsterName.ToLower())
                && tod.RepopTime != null)
            .OrderByDescending(tod => tod.Id)
            .Select(tod => (int?)tod.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
