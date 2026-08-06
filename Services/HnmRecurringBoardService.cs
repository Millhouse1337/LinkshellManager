using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Create/update (or disable) the standing "repeat the HNM signup board" template
// for a monster. Shared by the Activity and web event-create paths so both behave
// identically. The poller (HnmRecurringBoardBackgroundService) reads these rows and
// re-posts the board before the next predicted pop. Self-committing.
public static class HnmRecurringBoardService
{
    // Default lead when a board is switched on and no lead has ever been set for it.
    public const double DefaultLeadHours = 1;

    // Upsert the (LinkshellId, MonsterName) template from a just-created HNM event,
    // stamping the monster's CURRENT latest ToD so only a genuinely-new ToD triggers
    // the next recreation (no immediate spurious post for the pop this event is for).
    //
    // leadHours is normally null here: the event form only toggles recurrence on/off, and
    // the lead is owned by the End Camp / Post ToD form (which is where the officer knows
    // the next pop). Null therefore means "keep whatever lead this board already has" —
    // creating or editing an event must never quietly reset a lead set at End Camp.
    public static async Task UpsertAsync(
        ApplicationDbContext db, Event ev, double? leadHours, string? appUserId, CancellationToken cancellationToken)
    {
        var monster = ev.AssignedMonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monster))
        {
            return;
        }

        var board = await FindAsync(db, ev.LinkshellId, monster, cancellationToken);
        var now = DateTime.UtcNow;

        if (board is null)
        {
            board = new HnmRecurringBoard
            {
                LinkshellId = ev.LinkshellId,
                MonsterName = monster,
                CreatedByAppUserId = appUserId,
                CreatedAt = now
            };
            db.HnmRecurringBoards.Add(board);
        }

        board.Enabled = true;
        if (leadHours is { } lead && !double.IsNaN(lead))
        {
            board.LeadHours = Math.Clamp(lead, 0, 168);
        }
        else if (board.LeadHours <= 0 || double.IsNaN(board.LeadHours))
        {
            board.LeadHours = DefaultLeadHours;
        }
        board.PartySetupId = ev.PartySetupId;
        board.Details = ev.Details;
        board.EventLocation = ev.EventLocation;
        board.EventNameTemplate = ev.EventName;
        board.LastSourceTodId = await LatestTodIdAsync(db, ev.LinkshellId, monster, cancellationToken);
        board.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
    }

    // Apply the End Camp / Post ToD form's re-post choice: enable or disable this monster's
    // standing board and set its lead. Enabling seeds the template from THIS event (name,
    // party setup, location, details) so the re-posted board matches; disabling flips Enabled
    // off but keeps the row and its LastSourceTodId stamp.
    //
    // Unlike UpsertAsync this deliberately does NOT re-stamp LastSourceTodId. End Camp has just
    // logged the ToD that the poller must act on — stamping it here would mark the cycle handled
    // and the board would never re-post, which is the whole point of the form. Self-committing.
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
            board.EventNameTemplate = ev.EventName;
            board.PartySetupId = ev.PartySetupId;
            board.EventLocation = ev.EventLocation;
            board.Details = ev.Details;
        }
        board.UpdatedAt = nowUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    // Turn the template off (keep the row + its last-seen ToD stamp) when an officer
    // re-creates the board with the repeat option unchecked.
    public static async Task DisableAsync(
        ApplicationDbContext db, int linkshellId, string? monsterName, CancellationToken cancellationToken)
    {
        var monster = monsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monster))
        {
            return;
        }

        var board = await FindAsync(db, linkshellId, monster, cancellationToken);
        if (board is null || !board.Enabled)
        {
            return;
        }

        board.Enabled = false;
        board.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
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

    // The most recent ToD (by row id) for this monster that has a predicted pop time.
    // Used both as the upsert "last-seen" stamp and as the poller's target ToD, so the
    // two stay aligned and the poller doesn't recreate for the pop already handled.
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
