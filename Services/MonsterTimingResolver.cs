using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One monster's resolved setup for one linkshell: the linkshell's row when it has one, the
// HnmConfig-derived default when it doesn't.
//
// WindowCount and WindowCadenceMinutes describe the SPAWN GRID and are null together when a
// monster has none (Sky gods, most ground NMs) — null is a real answer here and deliberately not
// 1, because "no grid" and "one window" drive different board behaviour.
//
// TodIntervalMinutes is always present. For a grid monster it IS the cadence; for the rest it is
// what the ToD form's Interval field suggests. They are one editable column in the UI, and kept
// apart here so a monster with no grid can carry an interval without accidentally teaching the
// window auto-advance that it has one.
public readonly record struct MonsterTiming(
    string MonsterName,
    int? WindowCount,
    int? WindowCadenceMinutes,
    int TodIntervalMinutes,
    int CooldownMinutes)
{
    public bool HasSpawnGrid => WindowCount is > 0 && WindowCadenceMinutes is > 0;
}

// A linkshell's whole monster catalog, resolved once and reused.
public sealed class MonsterTimingMap
{
    private readonly Dictionary<string, MonsterTiming> _byAlias;

    public MonsterTimingMap(int linkshellId, IReadOnlyList<LinkshellMonsterTiming> rows)
    {
        LinkshellId = linkshellId;
        Rows = rows;

        _byAlias = new Dictionary<string, MonsterTiming>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var timing = ToTiming(row);
            // Index EVERY spelling that refers to this spawn, so a caller holding "Fafnir",
            // "Nidhogg" or the combined label lands on the one row. Same semantics as
            // HnmRecurringBoardService.FindAsync, resolved once here instead of per call.
            foreach (var alias in HnmConfig.MonsterMatchNames(row.MonsterName))
            {
                // First row wins. The editor rejects two rows claiming one spawn, so this only
                // matters for data that predates it — and losing the later row beats throwing.
                _byAlias.TryAdd(alias, timing);
            }
        }
    }

    public int LinkshellId { get; }

    // The stored rows, in editor order. Empty when the linkshell has never opened the editor —
    // every lookup then falls through to the built-in defaults, i.e. exactly today's behaviour.
    public IReadOnlyList<LinkshellMonsterTiming> Rows { get; }

    public bool IsSeeded => Rows.Count > 0;

    // Never null: an unknown monster (a free-text ToD name, a monster added to the catalog after
    // this linkshell was seeded) resolves to its built-in default.
    public MonsterTiming For(string? monsterName)
    {
        if (!string.IsNullOrWhiteSpace(monsterName)
            && _byAlias.TryGetValue(monsterName.Trim(), out var found))
        {
            return found;
        }
        return FromDefaults(monsterName);
    }

    // The monster options for the create-event and Log ToD pickers. The rows are already stored in
    // merged form, so there is no CombinedMonsterOptions pass at the read sites any more.
    public IReadOnlyList<string> EventMonsterOptions =>
        Rows.Count > 0
            ? Rows.Select(row => row.MonsterName).ToList()
            : HnmConfig.CombinedMonsterOptions(ViewModels.TodManagerViewModel.SupportedMonsters);

    // Whether this linkshell may assign `monsterName`. Its OWN catalog, so a monster an officer
    // added under Monster setups is assignable everywhere a built-in one is — which is what makes
    // "add a custom monster" mean "camp it", rather than only "give it a ToD cooldown".
    //
    // Blank is allowed. Every caller treats an empty monster as "unassigned", which is a valid
    // state and not something to reject.
    public bool Allows(string? monsterName)
    {
        var trimmed = monsterName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        // Against the ALIAS index, not EventMonsterOptions. Merge pairs are stored as one combined
        // row ("Fafnir/Nidhogg"), so the picker's own labels are combined too — a plain Contains
        // over them would reject the perfectly valid "Fafnir" that every existing party setup,
        // channel route and Discord board already holds. _byAlias indexes both halves and the
        // combined label, which is exactly the question being asked here.
        if (_byAlias.ContainsKey(trimmed))
        {
            return true;
        }

        // Unseeded: no rows to index, so fall back to the built-in catalog the picker is showing,
        // expanded the same way so "Fafnir" matches its combined "Fafnir/Nidhogg" label there too.
        return !IsSeeded
            && EventMonsterOptions.Any(option =>
                HnmConfig.MonsterMatchNames(option).Contains(trimmed, StringComparer.OrdinalIgnoreCase));
    }

    public static MonsterTiming FromDefaults(string? monsterName)
    {
        var name = string.IsNullOrWhiteSpace(monsterName) ? string.Empty : monsterName.Trim();
        var fallback = MonsterTimingDefaults.Build(name);
        return new MonsterTiming(
            name,
            fallback.WindowCount,
            fallback.WindowCount is null ? null : fallback.WindowCadenceMinutes,
            fallback.WindowCadenceMinutes ?? MonsterTimingDefaults.DefaultIntervalMinutes(name),
            fallback.CooldownMinutes);
    }

    private static MonsterTiming ToTiming(LinkshellMonsterTiming row)
    {
        // A row with a window count but no cadence (or the reverse) is half a grid, which nothing
        // downstream can use — treat it as no grid rather than dividing by zero later.
        var hasGrid = row.WindowCount is > 0 && row.WindowCadenceMinutes is > 0;
        return new MonsterTiming(
            row.MonsterName,
            hasGrid ? row.WindowCount : null,
            hasGrid ? row.WindowCadenceMinutes : null,
            row.WindowCadenceMinutes is > 0
                ? row.WindowCadenceMinutes.Value
                : MonsterTimingDefaults.DefaultIntervalMinutes(row.MonsterName),
            row.CooldownMinutes > 0
                ? row.CooldownMinutes
                : MonsterTimingDefaults.DefaultCooldownMinutes(row.MonsterName));
    }
}

// Loads (and memoizes, per request) a linkshell's monster setups. Scoped, mirroring
// DkpPoolResolver: the callers that need it in a loop resolve the map once outside the loop.
//
// Deliberately NOT process-wide cached. The window-advance poller re-reads every 10s over a tiny
// table, and a shared cache is exactly how a cadence an officer just edited would keep marching
// the old grid.
public sealed class MonsterTimingResolver
{
    private readonly ApplicationDbContext _db;
    private readonly Dictionary<int, MonsterTimingMap> _cache = new();

    public MonsterTimingResolver(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<MonsterTimingMap> GetMapAsync(int linkshellId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(linkshellId, out var cached))
        {
            return cached;
        }

        var rows = await QueryRowsAsync(new[] { linkshellId }, cancellationToken);
        var map = new MonsterTimingMap(linkshellId, rows);
        _cache[linkshellId] = map;
        return map;
    }

    // One query for many linkshells, so a background service sweeping every live camp doesn't
    // issue a round trip per event.
    public async Task<IReadOnlyDictionary<int, MonsterTimingMap>> GetMapsAsync(
        IReadOnlyCollection<int> linkshellIds, CancellationToken cancellationToken)
    {
        var wanted = linkshellIds.Distinct().ToList();
        var missing = wanted.Where(id => !_cache.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            var rows = await QueryRowsAsync(missing, cancellationToken);
            var byLinkshell = rows
                .GroupBy(row => row.LinkshellId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<LinkshellMonsterTiming>)group.ToList());
            foreach (var id in missing)
            {
                _cache[id] = new MonsterTimingMap(
                    id,
                    byLinkshell.TryGetValue(id, out var found)
                        ? found
                        : Array.Empty<LinkshellMonsterTiming>());
            }
        }
        return wanted.ToDictionary(id => id, id => _cache[id]);
    }

    public async Task<MonsterTiming> ResolveAsync(int linkshellId, string? monsterName, CancellationToken cancellationToken)
        => (await GetMapAsync(linkshellId, cancellationToken)).For(monsterName);

    // Drop the memo after the editor changes a linkshell's rows inside the same request.
    public void Invalidate(int linkshellId) => _cache.Remove(linkshellId);

    private async Task<IReadOnlyList<LinkshellMonsterTiming>> QueryRowsAsync(
        IReadOnlyCollection<int> linkshellIds, CancellationToken cancellationToken) =>
        await _db.LinkshellMonsterTimings
            .AsNoTracking()
            .Where(row => linkshellIds.Contains(row.LinkshellId))
            .OrderBy(row => row.SortOrder)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken);
}
