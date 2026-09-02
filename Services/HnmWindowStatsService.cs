using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One window of one monster's spawn grid: how often it actually popped there.
// Percent is relative to that MONSTER's own recorded pops, so a row reads "Adamantoise pops on
// window 4 42% of the time" rather than as a share of every HNM.
public sealed record HnmWindowBar(int Window, int Count, double Percent);

// One monster's window distribution.
//
// WindowCount is the linkshell's configured grid for this monster (25 for the wyrms, 5 for the
// ToAU three, and so on), so the row always shows the WHOLE band — the empty windows are the
// finding as much as the busy ones. ColorClass is the same family colour the Claims donut paints
// with, so a monster looks the same on both tabs of the card.
public sealed record HnmWindowMonster(
    string MonsterName,
    string ColorClass,
    int TotalPops,
    int WindowCount,
    int PeakWindow,
    double PeakPercent,
    IReadOnlyList<HnmWindowBar> Bars);

public sealed record HnmWindowStats(IReadOnlyList<HnmWindowMonster> Monsters)
{
    public static readonly HnmWindowStats Empty = new(Array.Empty<HnmWindowMonster>());

    public int TotalPops => Monsters.Sum(monster => monster.TotalPops);
}

// Builds the Dashboard / Discord Activity "Window frequency" chart: for each HNM, which window of
// its spawn band it actually pops on.
//
// The source is Tod.PopWindow — the "Popped on window" answer on the Log ToD / End Camp forms,
// which HnmCampPopService also stamps automatically on every camp ended from a board (defaulting
// to the window the board was showing). So this reads real history rather than asking anyone to
// record something new.
//
// Deliberately NOT filtered to claimed pops, unlike the Claims donut beside it. Which window a
// monster spawned in is true whoever ended up killing it, and throwing away the pops another
// linkshell claimed would bias the distribution toward the windows this linkshell happens to win.
public sealed class HnmWindowStatsService
{
    private readonly ApplicationDbContext _context;
    private readonly MonsterTimingResolver _monsterTimings;

    public HnmWindowStatsService(ApplicationDbContext context, MonsterTimingResolver monsterTimings)
    {
        _context = context;
        _monsterTimings = monsterTimings;
    }

    public async Task<HnmWindowStats> BuildAsync(int? linkshellId, CancellationToken cancellationToken = default)
    {
        if (linkshellId is not { } id)
        {
            return HnmWindowStats.Empty;
        }

        var pops = await _context.Tods
            .AsNoTracking()
            .Where(tod => tod.LinkshellId == id && tod.MonsterName != null && tod.PopWindow != null)
            .Select(tod => new { tod.MonsterName, Window = tod.PopWindow!.Value })
            .ToListAsync(cancellationToken);

        // Same closed roster as the Claims donut: the 12 built-in HNMs and nothing else. A window
        // number only means something against a spawn GRID, and the custom monsters a linkshell
        // adds have none.
        //
        // Grouped by FAMILY rather than by NQ/HQ half, because the grid belongs to the spawn: an
        // Adamantoise and an Aspidochelone are the same 25 windows, and splitting them would halve
        // the sample without answering a different question.
        var byMonster = pops
            .Where(pop => HnmConfig.IsHnmTierMonster(pop.MonsterName))
            .Where(pop => pop.Window > 0)
            .GroupBy(pop => HnmConfig.ClaimGroupName(pop.MonsterName), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (byMonster.Count == 0)
        {
            return HnmWindowStats.Empty;
        }

        var timings = await _monsterTimings.GetMapAsync(id, cancellationToken);
        var monsters = new List<HnmWindowMonster>();

        foreach (var group in byMonster)
        {
            var total = group.Count();
            var counts = group
                .GroupBy(pop => pop.Window)
                .ToDictionary(windowGroup => windowGroup.Key, windowGroup => windowGroup.Count());

            // The configured band, widened if history holds a pop past the end of it — a grid an
            // officer shortened afterwards must not silently drop the pops recorded under the old
            // one, which would leave the percentages summing to less than 100.
            var configured = timings.For(group.Key).WindowCount ?? 0;
            var windowCount = Math.Max(configured, counts.Keys.Max());

            var bars = Enumerable.Range(1, windowCount)
                .Select(window =>
                {
                    var count = counts.TryGetValue(window, out var found) ? found : 0;
                    return new HnmWindowBar(window, count, Math.Round(count * 100.0 / total, 1));
                })
                .ToList();

            // Ties go to the EARLIER window, which is the one a camp would actually sit through.
            var peak = bars.OrderByDescending(bar => bar.Count).ThenBy(bar => bar.Window).First();

            monsters.Add(new HnmWindowMonster(
                group.Key,
                HnmClaimStatsService.ColorClassFor(group.Key),
                total,
                windowCount,
                peak.Window,
                peak.Percent,
                bars));
        }

        // Most-observed monster first: the rows with enough pops to mean something lead, and the
        // one-pop monsters (whose "100% on window 3" is noise) fall to the bottom.
        return new HnmWindowStats(monsters
            .OrderByDescending(monster => monster.TotalPops)
            .ThenBy(monster => monster.MonsterName, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }
}
