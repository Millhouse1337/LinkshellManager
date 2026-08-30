using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Materializes a linkshell's monster setups the first time it needs them, and carries the old
// per-linkshell ToD cooldown blob (Linkshell.TodMonsterTimings) across while doing it.
//
// Seeding is LAZY rather than a migration backfill for the reason DkpPoolProvisioner is: the
// defaults live in HnmConfig, and a SQL copy of them is a snapshot that drifts the first time a
// monster's band changes. It also keeps the deploy cheap — a table of ~28 rows per linkshell is
// written when someone actually opens the editor, not for every linkshell that ever existed.
//
// Nothing on a hot path calls this. An un-seeded linkshell resolves entirely from HnmConfig via
// MonsterTimingMap, which is byte-identical to how the app behaved before this table existed.
public sealed class LinkshellMonsterTimingProvisioner
{
    private readonly ApplicationDbContext _db;

    public LinkshellMonsterTimingProvisioner(ApplicationDbContext db)
    {
        _db = db;
    }

    // Returns the linkshell's rows, creating them from the built-in defaults (plus any legacy blob
    // overrides) if this is the first look. Idempotent: the unique index means a race loses at the
    // DB and we just re-read the winner's rows.
    public async Task<IReadOnlyList<LinkshellMonsterTiming>> EnsureSeededAsync(
        int linkshellId, CancellationToken cancellationToken)
    {
        var existing = await LoadAsync(linkshellId, cancellationToken);
        if (existing.Count > 0)
        {
            return existing;
        }

        var legacy = await _db.Linkshells
            .AsNoTracking()
            .Where(linkshell => linkshell.Id == linkshellId)
            .Select(linkshell => linkshell.TodMonsterTimings)
            .FirstOrDefaultAsync(cancellationToken);

        var seeded = BuildSeed(linkshellId, legacy, DateTime.UtcNow);
        _db.LinkshellMonsterTimings.AddRange(seeded);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return await LoadAsync(linkshellId, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request seeded first. Drop our attempt and take theirs.
            foreach (var row in seeded)
            {
                _db.Entry(row).State = EntityState.Detached;
            }
            return await LoadAsync(linkshellId, cancellationToken);
        }
    }

    private Task<List<LinkshellMonsterTiming>> LoadAsync(int linkshellId, CancellationToken cancellationToken) =>
        _db.LinkshellMonsterTimings
            .Where(row => row.LinkshellId == linkshellId)
            .OrderBy(row => row.SortOrder)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken);

    // The built-in catalog with the linkshell's old blob values laid over the top.
    //
    // Internal + static so it can be tested without a database, which is where the merge-pair fold
    // below actually needs the coverage.
    internal static List<LinkshellMonsterTiming> BuildSeed(int linkshellId, string? legacyBlob, DateTime nowUtc)
    {
        var rows = MonsterTimingDefaults.BuildAll()
            .Select(timing => MonsterTimingDefaults.ToEntity(timing, linkshellId, nowUtc))
            .ToList();

        var legacy = ActivityDataController.ParseTodMonsterTimings(legacyBlob);
        if (legacy.Count == 0)
        {
            return rows;
        }

        // Alias -> the seeded row it belongs to, so a blob entry saved per HALF ("Nidhogg") lands
        // on the merged row ("Fafnir/Nidhogg") rather than becoming a second row for one spawn.
        // The unique index cannot catch that — the two names are different strings — so the fold
        // has to happen here.
        var rowByAlias = new Dictionary<string, LinkshellMonsterTiming>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            foreach (var alias in HnmConfig.MonsterMatchNames(row.MonsterName))
            {
                rowByAlias.TryAdd(alias, row);
            }
        }

        // BASE HALF WINS. A linkshell that configured both halves of a pair has two entries for one
        // row; applying them in blob order would make the winner depend on JSON ordering. Ranking
        // the base half first makes it deterministic, and the base is the half the merged label
        // leads with.
        var ordered = legacy
            .OrderBy(entry => HnmConfig.MonsterMergePairs.Any(pair =>
                pair.Base.Equals(entry.MonsterName?.Trim(), StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
            .ToList();

        var claimed = new HashSet<LinkshellMonsterTiming>(ReferenceEqualityComparer.Instance);
        var nextSortOrder = rows.Count;
        foreach (var entry in ordered)
        {
            var name = entry.MonsterName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (rowByAlias.TryGetValue(name, out var target))
            {
                // Only the first (highest-ranked) entry for a row applies.
                if (!claimed.Add(target))
                {
                    continue;
                }
                ApplyLegacy(target, entry, nowUtc);
                continue;
            }

            // Not in the built-in catalog: a monster this linkshell added itself. Keep it, as a
            // custom row so the editor still lets them delete it.
            var custom = MonsterTimingDefaults.ToEntity(
                MonsterTimingDefaults.Build(name, MonsterTimingDefaults.NormalizeCategory(entry.Category), nextSortOrder++),
                linkshellId,
                nowUtc);
            custom.IsCustom = true;
            ApplyLegacy(custom, entry, nowUtc);
            rows.Add(custom);
            foreach (var alias in HnmConfig.MonsterMatchNames(custom.MonsterName))
            {
                rowByAlias.TryAdd(alias, custom);
            }
        }

        return rows;
    }

    // The old blob stored cooldown as fractional HOURS and interval as an (hours, minutes) pair;
    // both become canonical minutes here. A zero/absent interval leaves the seeded cadence alone
    // rather than blanking a monster's spawn grid.
    private static void ApplyLegacy(LinkshellMonsterTiming row, ActivityTodMonsterTimingDto entry, DateTime nowUtc)
    {
        var cooldownMinutes = (int)Math.Round(entry.CooldownHours * 60d, MidpointRounding.AwayFromZero);
        if (cooldownMinutes > 0)
        {
            row.CooldownMinutes = cooldownMinutes;
        }

        var intervalMinutes = (entry.IntervalHours * 60) + entry.IntervalMinutes;
        if (intervalMinutes > 0)
        {
            row.WindowCadenceMinutes = intervalMinutes;
        }

        if (!string.IsNullOrWhiteSpace(entry.Category))
        {
            row.Category = MonsterTimingDefaults.NormalizeCategory(entry.Category);
        }

        row.UpdatedAtUtc = nowUtc;
    }
}
