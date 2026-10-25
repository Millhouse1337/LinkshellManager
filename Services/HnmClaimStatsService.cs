using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One row of the "HNM claim rate" chart: a monster, how many of its pops the linkshell claimed
// (Count) out of how many it went for (Attempts), and the two as a percentage (ClaimRate). An
// attempt is any ToD whose outcome was recorded, Claimed or Unclaimed; a ToD left "Not specified"
// says nothing either way, so it is not one.
//
// Percent is the monster's share of the window's CLAIMS, for the old Razor dashboard's donut; it is
// 0 for a monster the linkshell went for but never claimed.
//
// The three NQ/HQ families chart as TWO rows each — Adamantoise beside Aspidochelone, Behemoth
// beside King Behemoth, Fafnir beside Nidhogg — because which one popped is the whole point of a
// wyrm camp and folding them together hid it. Both halves carry their family's ColorClass so they
// read as one monster split in two; IsHq is what the surfaces badge differently.
// HasHqVariant is false for the six monsters that have no stronger half, which get no badge.
public sealed record HnmClaimSlice(
    string MonsterName,
    int Count,
    int Attempts,
    double ClaimRate,
    double Percent,
    string ColorClass,
    bool IsHq,
    bool HasHqVariant);

// The chart's three windows, all built from a single query.
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

// Builds the Dashboard / Discord Activity "HNM claim rate" chart.
//
// Both surfaces used to aggregate this themselves off whatever ToD rows they already happened
// to be holding for other cards — the web off its 200-row Recent Activity page, the Activity
// off the overview's 25 most recent ToDs of ANY monster, claimed or not. So the chart only ever
// saw a tail: a linkshell whose last 25 pops were Sky farm NMs charted zero HNM claims, and the
// Activity's "All" toggle could not mean all, because the rows were never sent. It is one query
// over the claimed and unclaimed ToDs now, and the same one on both surfaces.
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
        TodCatalog.SupportedMonsters
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

    // One claimed or unclaimed ToD, resolved to the half that actually popped.
    private sealed record ClaimRow(string Family, string Name, bool IsHq, bool HasHqVariant, bool Claimed, DateTime? When);

    private readonly ApplicationDbContext _context;

    public HnmClaimStatsService(ApplicationDbContext context)
    {
        _context = context;
    }

    // The longest custom range the chart offers: ten years, which is All in practice. A guard on the
    // date maths, not a product limit.
    public const int MaxCustomDays = 3650;

    public async Task<HnmClaimStats> BuildAsync(int? linkshellId, CancellationToken cancellationToken = default)
    {
        if (linkshellId is not { } id)
        {
            return HnmClaimStats.Empty;
        }

        var hnmOutcomes = await LoadOutcomesAsync(id, cancellationToken);
        var now = DateTime.UtcNow;
        return new HnmClaimStats(
            BuildSlices(hnmOutcomes, now.AddDays(-7)),
            BuildSlices(hnmOutcomes, now.AddDays(-30)),
            BuildSlices(hnmOutcomes, cutoff: null));
    }

    // One more range on top of the three the overview carries: the chart's "..." → last N days.
    public async Task<IReadOnlyList<HnmClaimSlice>> BuildLastDaysAsync(
        int linkshellId, int days, CancellationToken cancellationToken = default)
    {
        var hnmOutcomes = await LoadOutcomesAsync(linkshellId, cancellationToken);
        return BuildSlices(hnmOutcomes, DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, MaxCustomDays)));
    }

    private async Task<List<ClaimRow>> LoadOutcomesAsync(int id, CancellationToken cancellationToken)
    {
        // The linkshell's ToDs with a recorded outcome: claimed (a claim) or unclaimed (an attempt
        // that someone else won). The HNM test is a name-SEGMENT match (a stored name may be a
        // combined "Base/Stronger" label), which no provider can translate, so the monster
        // filtering happens below rather than in SQL.
        var claims = await _context.Tods
            .AsNoTracking()
            .Where(tod => tod.LinkshellId == id && tod.Claim != null && tod.MonsterName != null)
            .Select(tod => new { tod.MonsterName, tod.Hq, Claimed = tod.Claim == true, When = tod.Time ?? tod.TimeStamp })
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
        return claims
            .Where(claim => HnmConfig.IsHnmTierMonster(claim.MonsterName))
            .Select(claim =>
            {
                var half = HnmConfig.ResolveClaimHalf(claim.MonsterName, claim.Hq);
                return new ClaimRow(
                    HnmConfig.ClaimGroupName(claim.MonsterName),
                    half.Name,
                    half.IsHq,
                    half.HasHqVariant,
                    claim.Claimed,
                    claim.When);
            })
            .ToList();
    }

    private static IReadOnlyList<HnmClaimSlice> BuildSlices(
        IReadOnlyList<ClaimRow> claims,
        DateTime? cutoff)
    {
        // An undated ToD (no Time and no TimeStamp) can't be placed in a dated window, so it
        // counts only toward All — never silently against 7d/30d.
        var inWindow = cutoff is { } from
            ? claims.Where(claim => claim.When >= from).ToList()
            : claims;

        if (inWindow.Count == 0)
        {
            return Array.Empty<HnmClaimSlice>();
        }
        var totalClaims = inWindow.Count(claim => claim.Claimed);

        // EVERY monster gets a row, including one the linkshell went for and never claimed (0 of
        // n). Truncating to a top N is what "the chart is missing HNMs" used to look like. Past the
        // palette the colours repeat; every monster is still named.
        var entries = inWindow
            .GroupBy(claim => claim.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Name = group.Key,
                Count = group.Count(claim => claim.Claimed),
                Attempts = group.Count(),
                group.First().Family,
                group.First().IsHq,
                group.First().HasHqVariant,
            })
            .ToList();

        // Ordered by FAMILY, most-camped family first, NQ before its HQ — so a pair's two rows are
        // always neighbours. Ordering the halves by their own counts would scatter them (a
        // 7-Nidhogg season sits at the top, its 5 Fafnirs three rows later), and the shared hue
        // only reads as "one monster, two outcomes" when the two are side by side.
        var attemptsByFamily = entries
            .GroupBy(entry => entry.Family, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Attempts), StringComparer.OrdinalIgnoreCase);

        return entries
            .OrderByDescending(entry => attemptsByFamily[entry.Family])
            .ThenBy(entry => entry.Family, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.IsHq)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new HnmClaimSlice(
                entry.Name,
                entry.Count,
                entry.Attempts,
                Math.Round(entry.Count * 100.0 / entry.Attempts, 1),
                totalClaims == 0 ? 0 : Math.Round(entry.Count * 100.0 / totalClaims, 1),
                // By family, so a pair's NQ and HQ share a hue.
                ColorClassFor(entry.Family),
                entry.IsHq,
                entry.HasHqVariant))
            .ToList();
    }
}
