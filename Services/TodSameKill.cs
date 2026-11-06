using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Two characters at one kill can both post its ToD from the addon. Each dates it off its own clock,
// so the times are seconds apart rather than equal. Those posts are one kill: saved as two rows they
// gave the monster two repops and the Window frequency chart counted the kill twice.
//
// The first post's time and repop stand, because they are already showing everywhere. A later post
// only fills in what the first didn't know.
public static class TodSameKill
{
    // Wide enough for two PCs' clocks to disagree, and shorter than any real respawn: the shortest
    // cooldown a ToD can have is 5 minutes.
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    public static Task<Tod?> FindAsync(
        ApplicationDbContext db, int linkshellId, string? monster, DateTime defeatedAtUtc, CancellationToken cancellationToken)
    {
        var names = HnmConfig.MonsterMatchNamesLower(monster);
        if (names.Count == 0)
        {
            return Task.FromResult<Tod?>(null);
        }
        var from = defeatedAtUtc - Tolerance;
        var to = defeatedAtUtc + Tolerance;
        return db.Tods
            .Where(tod => tod.LinkshellId == linkshellId
                && tod.MonsterName != null
                && names.Contains(tod.MonsterName.ToLower())
                && tod.Time >= from
                && tod.Time <= to)
            .OrderBy(tod => tod.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public static Task<PendingTodSubmission?> FindPendingAsync(
        ApplicationDbContext db, int linkshellId, string? monster, DateTime defeatedAtUtc, CancellationToken cancellationToken)
    {
        var names = HnmConfig.MonsterMatchNamesLower(monster);
        if (names.Count == 0)
        {
            return Task.FromResult<PendingTodSubmission?>(null);
        }
        var from = defeatedAtUtc - Tolerance;
        var to = defeatedAtUtc + Tolerance;
        return db.PendingTodSubmissions
            .Where(pending => pending.LinkshellId == linkshellId
                && pending.MonsterName != null
                && names.Contains(pending.MonsterName.ToLower())
                && pending.Time >= from
                && pending.Time <= to)
            .OrderBy(pending => pending.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // A second post's answers onto the saved ToD. Stages only; true when something changed.
    public static bool Merge(Tod tod, bool? claim, int? popWindow)
    {
        var changed = false;
        if (tod.Claim is null && claim is not null)
        {
            tod.Claim = claim;
            tod.TotalClaims = claim == true ? 1 : 0;
            changed = true;
        }
        var window = EarliestWindow(tod.PopWindow, popWindow);
        if (window != tod.PopWindow)
        {
            tod.PopWindow = window;
            changed = true;
        }
        return changed;
    }

    // Each character's window is when IT first noticed the pop, which can only be at or after the
    // real one. So the earliest reading is the closest to the truth, and any reading beats none.
    public static int? EarliestWindow(int? first, int? second) =>
        first is { } a && second is { } b ? Math.Min(a, b) : first ?? second;
}
