using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// "Which pops is this linkshell still waiting on?" — the ToD rows whose predicted RepopTime is
// still in the future, one per spawn.
//
// Exists so the create-event form can pre-fill Start with the pop the officer is almost certainly
// creating the camp for. That is the same instant HnmAutoEventService stamps on the event it
// creates from a ToD (StartTime = Tod.RepopTime), so a hand-made camp and an auto-made one land
// on the same schedule instead of drifting by however precisely the officer retyped the time.
//
// Shared by the web event form and the Activity's create-event modal so both answer it the same
// way — in particular the merge-pair matching, which a client-side lookup keyed on the raw stored
// name would get wrong (a "Fafnir" ToD is the pop a "Fafnir/Nidhogg" camp is waiting on).
public static class UpcomingRepopLookup
{
    // MatchNames carries every spelling of the spawn (HnmConfig.MonsterMatchNames), so a caller
    // can match a picked monster against it with a plain case-insensitive contains and never
    // needs its own copy of the merge-pair table.
    public sealed record Entry(
        int TodId,
        string MonsterName,
        IReadOnlyList<string> MatchNames,
        DateTime RepopTimeUtc,
        int? DayNumber);

    public static async Task<List<Entry>> ForLinkshellAsync(
        ApplicationDbContext db,
        int linkshellId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (linkshellId <= 0)
        {
            return new List<Entry>();
        }

        var rows = await db.Tods
            .AsNoTracking()
            .Where(tod => tod.LinkshellId == linkshellId
                && tod.MonsterName != null
                && tod.RepopTime != null
                && tod.RepopTime > nowUtc)
            // Newest row first, so a monster that was logged twice answers with its LATEST
            // ToD — the same row the recurring-board poller acts on (see
            // HnmRecurringBoardService.SyncParkedBoardsForTodAsync).
            .OrderByDescending(tod => tod.Id)
            .Select(tod => new { tod.Id, tod.MonsterName, tod.RepopTime, tod.DayNumber })
            .ToListAsync(cancellationToken);

        var entries = new List<Entry>();
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var matchNames = HnmConfig.MonsterMatchNames(row.MonsterName).ToList();
            if (matchNames.Count == 0)
            {
                continue;
            }

            // A newer ToD already speaks for this spawn under one of its other spellings
            // ("Fafnir" vs "Fafnir/Nidhogg"), so this older row is superseded.
            if (matchNames.Any(answered.Contains))
            {
                continue;
            }

            foreach (var name in matchNames)
            {
                answered.Add(name);
            }

            entries.Add(new Entry(
                row.Id,
                row.MonsterName!.Trim(),
                matchNames,
                DateTime.SpecifyKind(row.RepopTime!.Value, DateTimeKind.Utc),
                row.DayNumber));
        }

        return entries.OrderBy(entry => entry.RepopTimeUtc).ToList();
    }
}
