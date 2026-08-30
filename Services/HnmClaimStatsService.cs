using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One slice of the "HNM Claims" donut: a monster and the share of the window's claims it took.
public sealed record HnmClaimSlice(string MonsterName, int Count, double Percent, string ColorClass);

// The donut's three windows, all built from a single query.
public sealed record HnmClaimStats(
    IReadOnlyList<HnmClaimSlice> Last7Days,
    IReadOnlyList<HnmClaimSlice> Last30Days,
    IReadOnlyList<HnmClaimSlice> AllTime)
{
    public static readonly HnmClaimStats Empty = new(
        Array.Empty<HnmClaimSlice>(),
        Array.Empty<HnmClaimSlice>(),
        Array.Empty<HnmClaimSlice>());
}

// Builds the Dashboard / Discord Activity "HNM Claims" donut.
//
// Both surfaces used to aggregate this themselves off whatever ToD rows they already happened
// to be holding for other cards — the web off its 200-row Recent Activity page, the Activity
// off the overview's 25 most recent ToDs of ANY monster, claimed or not. So the chart only ever
// saw a tail: a linkshell whose last 25 pops were Sky farm NMs charted zero HNM claims, and the
// Activity's "All" toggle could not mean all, because the rows were never sent. It is one query
// over the claimed ToDs now, and the same one on both surfaces.
public sealed class HnmClaimStatsService
{
    // One colour per chartable monster — 6 long-window HNMs plus the 3 NQ/HQ families, so the
    // donut can never need a tenth. The old 6-colour palette was why both surfaces truncated to
    // a top 6 and silently dropped the rest of the legend.
    private static readonly string[] PaletteClasses =
        { "a", "b", "c", "d", "e", "f", "g", "h", "i" };

    private readonly ApplicationDbContext _context;

    public HnmClaimStatsService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<HnmClaimStats> BuildAsync(int? linkshellId, CancellationToken cancellationToken = default)
    {
        if (linkshellId is not { } id)
        {
            return HnmClaimStats.Empty;
        }

        // Two columns of the linkshell's claimed ToDs. The HNM test is a name-SEGMENT match (a
        // stored name may be a combined "Base/Stronger" label), which no provider can translate,
        // so the monster filtering happens below rather than in SQL.
        var claims = await _context.Tods
            .AsNoTracking()
            .Where(tod => tod.LinkshellId == id && tod.Claim == true && tod.MonsterName != null)
            .Select(tod => new { tod.MonsterName, When = tod.Time ?? tod.TimeStamp })
            .ToListAsync(cancellationToken);

        // The chart is the 12 built-in HNMs and nothing else, which ClaimGroupName then collapses
        // into 9 entries (the three NQ/HQ families count as one monster each):
        //
        //   Adamantoise/Aspidochelone · Behemoth/King Behemoth · Cerberus · Fafnir/Nidhogg
        //   Hydra · Jormungand · Khimaira · Tiamat · Vrtra
        //
        // IsHnmTierMonster is the TIER question — the 6 long-window monsters plus the 3 NQ/HQ
        // families — and deliberately NOT IsTrueHnm/ShortWindowHnms, whose timed NMs (Capricious
        // Cassie / Bune / Boroka / Roc) merely share the kings' spawn band and are NMs by tier.
        // A linkshell's own custom monsters stay out too, however they are categorised: Sky farm
        // pops, ground NMs, HENMs and Sea NMs all flow through this table with Claim = true and
        // would otherwise dominate the chart.
        var hnmClaims = claims
            .Where(claim => HnmConfig.IsHnmTierMonster(claim.MonsterName))
            .Select(claim => (Monster: HnmConfig.ClaimGroupName(claim.MonsterName), claim.When))
            .ToList();

        var now = DateTime.UtcNow;
        return new HnmClaimStats(
            BuildSlices(hnmClaims, now.AddDays(-7)),
            BuildSlices(hnmClaims, now.AddDays(-30)),
            BuildSlices(hnmClaims, cutoff: null));
    }

    private static IReadOnlyList<HnmClaimSlice> BuildSlices(
        IReadOnlyList<(string Monster, DateTime? When)> claims,
        DateTime? cutoff)
    {
        // An undated claim (no Time and no TimeStamp) can't be placed in a dated window, so it
        // counts only toward All — never silently against 7d/30d.
        var inWindow = cutoff is { } from
            ? claims.Where(claim => claim.When >= from).ToList()
            : claims;

        var total = inWindow.Count;
        if (total == 0)
        {
            return Array.Empty<HnmClaimSlice>();
        }

        // EVERY monster gets a slice. Truncating to a top N left the ring open and the
        // percentages short of 100 — which is exactly what "the chart is missing HNMs" looked
        // like. Past the palette the colours repeat; the legend still names every monster.
        return inWindow
            .GroupBy(claim => claim.Monster, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Monster = group.Key, Count = group.Count() })
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Monster, StringComparer.OrdinalIgnoreCase)
            .Select((entry, index) => new HnmClaimSlice(
                entry.Monster,
                entry.Count,
                Math.Round(entry.Count * 100.0 / total, 1),
                PaletteClasses[index % PaletteClasses.Length]))
            .ToList();
    }
}
