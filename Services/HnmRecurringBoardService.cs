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
    // Upsert the (LinkshellId, MonsterName) template from a just-created HNM event,
    // stamping the monster's CURRENT latest ToD so only a genuinely-new ToD triggers
    // the next recreation (no immediate spurious post for the pop this event is for).
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
        board.LeadHours = Math.Clamp(leadHours ?? 1, 0, 168);
        if (double.IsNaN(board.LeadHours)) { board.LeadHours = 1; }
        board.PartySetupId = ev.PartySetupId;
        board.Details = ev.Details;
        board.EventLocation = ev.EventLocation;
        board.EventNameTemplate = ev.EventName;
        board.LastSourceTodId = await LatestTodIdAsync(db, ev.LinkshellId, monster, cancellationToken);
        board.UpdatedAt = now;

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

    private static Task<HnmRecurringBoard?> FindAsync(
        ApplicationDbContext db, int linkshellId, string monster, CancellationToken cancellationToken)
    {
        var lower = monster.ToLower();
        return db.HnmRecurringBoards
            .FirstOrDefaultAsync(
                board => board.LinkshellId == linkshellId && board.MonsterName.ToLower() == lower,
                cancellationToken);
    }

    // The most recent ToD (by row id) for this monster that has a predicted pop time.
    // Used both as the upsert "last-seen" stamp and as the poller's target ToD, so the
    // two stay aligned and the poller doesn't recreate for the pop already handled.
    public static Task<int?> LatestTodIdAsync(
        ApplicationDbContext db, int linkshellId, string monster, CancellationToken cancellationToken)
    {
        var lower = monster.ToLower();
        return db.Tods
            .Where(tod => tod.LinkshellId == linkshellId
                && tod.MonsterName != null
                && tod.MonsterName.ToLower() == lower
                && tod.RepopTime != null)
            .OrderByDescending(tod => tod.Id)
            .Select(tod => (int?)tod.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
