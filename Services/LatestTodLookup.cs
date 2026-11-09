using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// "When was each monster last killed?" — the newest ToD row per spawn, past or future repop alike.
//
// Feeds the create-event form's "Last ToD" readout beside Starts. UpcomingRepopLookup only answers
// pops still to come, so a monster whose repop has already passed would show nothing there.
//
// "Newest" sorts on Time ?? TimeStamp, the same order the ToD page uses (todSortKey), so a ToD that
// was never entered still counts as the monster's latest and the form agrees with the ToD page.
// Server-side for the merge-pair matching: a "Fafnir" ToD is the last kill of a "Fafnir/Nidhogg" camp.
public static class LatestTodLookup
{
    // TimeUtc is null for a "Not entered" ToD (the camp ended without anyone seeing it die).
    public sealed record Entry(
        int TodId,
        string MonsterName,
        IReadOnlyList<string> MatchNames,
        DateTime? TimeUtc,
        bool Hq,
        int? DayNumber);

    public static async Task<List<Entry>> ForLinkshellAsync(
        ApplicationDbContext db,
        int linkshellId,
        CancellationToken cancellationToken)
    {
        if (linkshellId <= 0)
        {
            return new List<Entry>();
        }

        var rows = await db.Tods
            .AsNoTracking()
            .Where(tod => tod.LinkshellId == linkshellId && tod.MonsterName != null)
            .OrderByDescending(tod => tod.Time ?? tod.TimeStamp)
            .ThenByDescending(tod => tod.Id)
            .Select(tod => new { tod.Id, tod.MonsterName, tod.Time, tod.Hq, tod.DayNumber })
            .ToListAsync(cancellationToken);

        var entries = new List<Entry>();
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var matchNames = HnmConfig.MonsterMatchNames(row.MonsterName).ToList();
            // An older row of a spawn already answered under another spelling is superseded.
            if (matchNames.Count == 0 || matchNames.Any(answered.Contains))
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
                row.Time.HasValue ? DateTime.SpecifyKind(row.Time.Value, DateTimeKind.Utc) : null,
                row.Hq,
                row.DayNumber));
        }

        return entries;
    }
}
