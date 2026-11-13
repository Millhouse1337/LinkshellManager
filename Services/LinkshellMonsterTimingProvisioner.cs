using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Materializes a linkshell's monster setups the first time it needs them.
//
// (It used to lay the old per-linkshell ToD cooldown blob, Linkshell.TodMonsterTimings, over the
// defaults on that first seed. Every linkshell that had a blob was seeded long ago, so the import
// and the column went in Phase 4B.)
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

    // Returns the linkshell's rows, creating them from the built-in defaults if this is the first
    // look. Idempotent: the unique index means a race loses at the DB and we just re-read the
    // winner's rows.
    public async Task<IReadOnlyList<LinkshellMonsterTiming>> EnsureSeededAsync(
        int linkshellId, CancellationToken cancellationToken)
    {
        var existing = await LoadAsync(linkshellId, cancellationToken);
        if (existing.Count > 0)
        {
            return existing;
        }

        var seeded = BuildSeed(linkshellId, DateTime.UtcNow);
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

    // The built-in catalog, one row per monster.
    //
    // Internal + static so tests can build a seeded catalog without a database.
    internal static List<LinkshellMonsterTiming> BuildSeed(int linkshellId, DateTime nowUtc) =>
        MonsterTimingDefaults.BuildAll()
            .Select(timing => MonsterTimingDefaults.ToEntity(timing, linkshellId, nowUtc))
            .ToList();
}
