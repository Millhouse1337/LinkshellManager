using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One slice of the "HNM Claims" donut: a monster and the share of the window's claims it took.
//
// The three NQ/HQ families chart as TWO slices each — Adamantoise beside Aspidochelone, Behemoth
// beside King Behemoth, Fafnir beside Nidhogg — because which one popped is the whole point of a
// wyrm camp and folding them together hid it. Both halves carry their family's ColorClass so they
// read as one monster split in two; IsHq is what the surfaces shade and badge differently.
// HasHqVariant is false for the six monsters that have no stronger half, which get no badge.
public sealed record HnmClaimSlice(
    string MonsterName,
    int Count,
    double Percent,
    string ColorClass,
    bool IsHq,
    bool HasHqVariant);

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
    //
    // Nine is still right with the families split in two: a pair's NQ and HQ SHARE their family's
    // colour and are told apart by shading, so 12 slices still only ever need 9 hues.
    private static readonly string[] PaletteClasses =
        { "a", "b", "c", "d", "e", "f", "g", "h", "i" };

    // Colour is fixed per FAMILY off the built-in catalog, not handed out in each window's count
    // order. Two reasons: a pair's two halves have to land on the same hue for the NQ/HQ shading
    // to read as one monster split in two, and a monster now keeps its colour when you flip
    // 7d / 30d / All — rank-ordered assignment repainted the whole chart on every toggle.
    private static readonly IReadOnlyDictionary<string, string> ColorClassByFamily =
        ViewModels.TodManagerViewModel.SupportedMonsters
            .Select(HnmConfig.ClaimGroupName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((family, index) => (family, color: PaletteClasses[index % PaletteClasses.Length]))
            .ToDictionary(entry => entry.family, entry => entry.color, StringComparer.OrdinalIgnoreCase);

    // The family's palette letter, so the Window-frequency chart on the other tab of the same card
    // paints each monster the colour its slice already has here. The roster is closed, so the
    // fallback is unreachable — it exists so an unmapped name can never crash a dashboard.
    public static string ColorClassFor(string? monsterName) =>
        ColorClassByFamily.TryGetValue(HnmConfig.ClaimGroupName(monsterName), out var color)
            ? color
            : PaletteClasses[0];

    // One claimed ToD, resolved to the half that actually popped.
    private sealed record ClaimRow(string Family, string Name, bool IsHq, bool HasHqVariant, DateTime? When);

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
            .Select(tod => new { tod.MonsterName, tod.Hq, When = tod.Time ?? tod.TimeStamp })
            .ToListAsync(cancellationToken);

        // The chart is the 12 built-in HNMs and nothing else — charted as up to 12 slices, since
        // each NQ/HQ family splits into the half that actually popped:
        //
        //   Adamantoise · Aspidochelone · Behemoth · King Behemoth · Cerberus · Fafnir · Nidhogg
        //   Hydra · Jormungand · Khimaira · Tiamat · Vrtra
        //
        // ClaimGroupName is still the FAMILY (the two halves share a colour); ResolveClaimHalf is
        // the slice. They used to be the same thing, which meant a season of Nidhoggs and a season
        // of Fafnirs charted identically.
        //
        // IsHnmTierMonster is the TIER question — the 6 long-window monsters plus the 3 NQ/HQ
        // families — and deliberately NOT IsTrueHnm/ShortWindowHnms, whose timed NMs (Capricious
        // Cassie / Bune / Boroka / Roc) merely share the kings' spawn band and are NMs by tier.
        // A linkshell's own custom monsters stay out too, however they are categorised: Sky farm
        // pops, ground NMs, HENMs and Sea NMs all flow through this table with Claim = true and
        // would otherwise dominate the chart.
        var hnmClaims = claims
            .Where(claim => HnmConfig.IsHnmTierMonster(claim.MonsterName))
            .Select(claim =>
            {
                var half = HnmConfig.ResolveClaimHalf(claim.MonsterName, claim.Hq);
                return new ClaimRow(
                    HnmConfig.ClaimGroupName(claim.MonsterName),
                    half.Name,
                    half.IsHq,
                    half.HasHqVariant,
                    claim.When);
            })
            .ToList();

        var now = DateTime.UtcNow;
        return new HnmClaimStats(
            BuildSlices(hnmClaims, now.AddDays(-7)),
            BuildSlices(hnmClaims, now.AddDays(-30)),
            BuildSlices(hnmClaims, cutoff: null));
    }

    private static IReadOnlyList<HnmClaimSlice> BuildSlices(
        IReadOnlyList<ClaimRow> claims,
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
        var entries = inWindow
            .GroupBy(claim => claim.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Name = group.Key,
                Count = group.Count(),
                group.First().Family,
                group.First().IsHq,
                group.First().HasHqVariant,
            })
            .ToList();

        // Ordered by FAMILY, busiest family first, NQ before its HQ — so a pair's two arcs are
        // always neighbours on the ring. Ordering the halves by their own counts would scatter
        // them (a 7-Nidhogg season sits at the top, its 5 Fafnirs three slices later), and the
        // shared-hue-lighter-shade shading only reads as "one monster, two outcomes" when the two
        // are side by side. Within a family the tie-break is NQ first, which is also the order the
        // legend explains them in.
        var totalByFamily = entries
            .GroupBy(entry => entry.Family, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Count), StringComparer.OrdinalIgnoreCase);

        return entries
            .OrderByDescending(entry => totalByFamily[entry.Family])
            .ThenBy(entry => entry.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.IsHq)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new HnmClaimSlice(
                entry.Name,
                entry.Count,
                Math.Round(entry.Count * 100.0 / total, 1),
                // By family, so a pair's NQ and HQ share a hue.
                ColorClassFor(entry.Family),
                entry.IsHq,
                entry.HasHqVariant))
            .ToList();
    }
}
