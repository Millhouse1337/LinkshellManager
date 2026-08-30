using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
using LinkshellManagerDiscordApp.ViewModels;

namespace LinkshellManagerDiscordApp.Services;

// The built-in monster setups a linkshell starts from, and the fallback every lookup lands on when
// a linkshell has no row for a monster (or no rows at all — see LinkshellMonsterTimingProvisioner,
// which materializes these lazily rather than in a migration).
//
// Defaults are DERIVED from HnmConfig rather than copied beside it. A SQL backfill or a second
// hardcoded table would be a snapshot that drifts the first time a monster's band changes; this
// re-reads the same sets the runtime does, so there is nothing to keep in sync.
public static class MonsterTimingDefaults
{
    public const string HnmCategory = "HNMs";
    public const string OtherCategory = "Other NMs";

    // (SkyCategory lived here. The eight Sky farm NMs were seeded as their own heading; they were
    // never camped as events and only padded the editor and the ToD picker, so the category is
    // gone — see the RemoveSkyNmMonsterTimings migration, which drops the rows already written.
    // HnmConfig.SkyFarmNms survives: it still answers the 2-hour cooldown for a free-text or
    // addon-posted Sky ToD, and it still draws the Charts → Sky board.)
    //
    // OtherCategory now seeds NOTHING — the built-in catalog is the twelve HNMs (see
    // TodManagerViewModel.SupportedMonsters). It stays in this list precisely because it is empty:
    // the editor renders a heading per category, so this is the "+ Add monster" landing pad for
    // every NM a linkshell camps. Drop it and there is nowhere to put one.
    public static readonly IReadOnlyList<string> Categories = new[] { HnmCategory, OtherCategory };

    // Everything a monster row carries before a linkshell touches it. WindowCount is null for a
    // monster with no spawn grid — a real answer, and deliberately not 1.
    public readonly record struct DefaultTiming(
        string MonsterName,
        int? WindowCount,
        int? WindowCadenceMinutes,
        int CooldownMinutes,
        string Category,
        int SortOrder);

    // The seeded monster set: the merged create-event catalog, and nothing else. The table and the
    // dropdown are then the same list by construction.
    //
    // In practice that is the twelve HNMs, so every row this returns files under HnmCategory and
    // "Other NMs" seeds empty. That is the point — an NM is a linkshell's own choice, added with
    // "+ Add monster", not a name this file guesses at.
    //
    // The Sky farm NMs used to be unioned in on top of it, because they carry a configurable ToD
    // cooldown while having no spawn window. They are no longer seeded at all — a linkshell that
    // wants one adds it as a custom row, and a Sky ToD typed through the picker's "Other" branch
    // still resolves its 2-hour cooldown from HnmConfig via Build/DefaultCooldownMinutes.
    public static IReadOnlyList<DefaultTiming> BuildAll()
    {
        var names = new List<(string Name, string Category)>();

        foreach (var monster in HnmConfig.CombinedMonsterOptions(TodManagerViewModel.SupportedMonsters))
        {
            names.Add((monster, CategoryFor(monster)));
        }

        // Grouped for display, HNMs first, and stable — SortOrder is persisted, so a later change
        // to this ordering only affects linkshells seeded afterwards. OrderBy is a stable sort, so
        // each category keeps the catalog order it was collected in.
        var ordered = names
            .OrderBy(entry => CategoryRank(entry.Category))
            .ToList();

        var results = new List<DefaultTiming>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var (name, category) = ordered[index];
            results.Add(Build(name, category, index));
        }
        return results;
    }

    // The default setup for ONE monster, including a monster that isn't in the seeded catalog at
    // all (a custom row whose overrides were cleared, or a lookup for a free-text ToD name).
    public static DefaultTiming Build(string monsterName, string? category = null, int sortOrder = 0)
    {
        var cadence = HnmConfig.DefaultWindowCadence(monsterName);
        return new DefaultTiming(
            monsterName,
            cadence?.Windows,
            // A monster off the spawn grid still gets a cadence: it is what the ToD form's Interval
            // suggests. The two were always the same number stored twice (1 Hour vs 60 min), which
            // is exactly why this table has one column for them.
            cadence?.Minutes ?? DefaultIntervalMinutes(monsterName),
            DefaultCooldownMinutes(monsterName),
            category ?? CategoryFor(monsterName),
            sortOrder);
    }

    // Which heading a monster sorts under. Tier, not cadence: the timed NMs (Capricious Cassie,
    // Bune, Boroka, Roc) run the kings' 7 x 10-min band but are still NMs, the same cut
    // HnmConfig.IsHnmTierMonster documents.
    //
    // Two headings only. A Sky farm NM added back as a custom row files under Other NMs like any
    // other untimed spawn — there is no Sky heading to sort it under any more.
    public static string CategoryFor(string? monsterName) =>
        HnmConfig.IsHnmTierMonster(monsterName) ? HnmCategory : OtherCategory;

    // Display order of the headings; an unknown category sorts last (and renders as Other NMs).
    public static int CategoryRank(string? category)
    {
        for (var index = 0; index < Categories.Count; index++)
        {
            if (Categories[index].Equals(category?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return Categories.Count;
    }

    // An unrecognised or retired category still renders, folded into Other NMs, so a stale row
    // stays visible and deletable instead of vanishing from the editor.
    public static string NormalizeCategory(string? category) =>
        Categories.FirstOrDefault(known => known.Equals(category?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? OtherCategory;

    // Tiamat / Jormungand / Vrtra, and the ToAU three. Both are the hour the spawn window OPENS,
    // not when it closes — the 25 × 60-min band then runs for a further 24h on top. Named because
    // the ToAU value is what the AdjustToauHnmCooldowns migration rewrites, and a test holds the
    // two side by side.
    public const int WyrmCooldownMinutes = 84 * 60;
    public const int ToauCooldownMinutes = 48 * 60;

    // Repop cooldown in minutes. This is the canonical owner of the fact — the string-labelled
    // ActivityDataController.GetDefaultTodCooldown formats this rather than repeating it.
    //
    // Tolerant of a combined "Base/Stronger" label: the merge families all sit on the same 22h
    // band, but reading segments keeps that a consequence of the data rather than a coincidence
    // the code relies on.
    public static int DefaultCooldownMinutes(string? monsterName)
    {
        foreach (var segment in HnmConfig.MonsterSegments(monsterName))
        {
            // The ToAU three first, because they are a subset of LongWindowHnms below. Whichever
            // of the two sets a monster belongs to now decides its band — this used to name
            // Tiamat / Jormungand / Vrtra by hand and let "everything else long-window" fall
            // through, which is how a new long-window monster would silently inherit the wrong one.
            if (HnmConfig.ToauHnms.Contains(segment))
            {
                return ToauCooldownMinutes;
            }
            if (HnmConfig.LongWindowHnms.Contains(segment))
            {
                return WyrmCooldownMinutes;
            }
            // Bloodsucker repops on a 71-hour cycle, unlike the other ground NMs it's grouped with.
            // It is no longer a built-in — like the Sky farm NMs below, this survives so a linkshell
            // that adds it back, or an addon-posted free-text ToD, still starts from the right band
            // instead of the 22h catch-all.
            if (segment.Equals("Bloodsucker", StringComparison.OrdinalIgnoreCase))
            {
                return 71 * 60;
            }
            if (HnmConfig.SkyGods.Contains(segment) || HnmConfig.SeaNms.Contains(segment))
            {
                return 5;
            }
            if (HnmConfig.SkyFarmNms.Contains(segment))
            {
                return 2 * 60;
            }
        }
        return 22 * 60;
    }

    // How often the ToD form suggests re-checking a monster with no spawn grid.
    public static int DefaultIntervalMinutes(string? monsterName) =>
        HnmConfig.MonsterSegments(monsterName).Any(HnmConfig.LongWindowHnms.Contains) ? 60 : 10;

    public static LinkshellMonsterTiming ToEntity(DefaultTiming timing, int linkshellId, DateTime nowUtc) => new()
    {
        LinkshellId = linkshellId,
        MonsterName = timing.MonsterName,
        NormalizedMonsterName = Normalize(timing.MonsterName),
        WindowCount = timing.WindowCount,
        WindowCadenceMinutes = timing.WindowCadenceMinutes,
        CooldownMinutes = timing.CooldownMinutes,
        Category = timing.Category,
        IsCustom = false,
        SortOrder = timing.SortOrder,
        CreatedAtUtc = nowUtc,
        UpdatedAtUtc = nowUtc,
    };

    public static string Normalize(string? monsterName) =>
        (monsterName ?? string.Empty).Trim().ToLowerInvariant();

    // Convenience for the display side: the label form of a default cooldown, which is what the
    // ToD forms still store.
    public static string DefaultCooldownLabel(string? monsterName) =>
        TodDurationFormat.Format(DefaultCooldownMinutes(monsterName));

    public static string DefaultIntervalLabel(string? monsterName) =>
        TodDurationFormat.Format(HnmConfig.DefaultWindowCadence(monsterName)?.Minutes
            ?? DefaultIntervalMinutes(monsterName));
}
