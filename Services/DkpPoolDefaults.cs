using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Every linkshell gets exactly one pool the moment it exists: "Default", mapped to nothing. An
// empty mapping means every event type falls through to it, so a linkshell that never touches the
// DKP grouping card behaves exactly as it did before pools existed.
//
// The seeded group is NAMED rather than left blank on purpose: DkpPoolEditor treats a blank name
// as "this row doesn't exist" (it drops the pool and repoints its ledger rows), and the DB has a
// required Name with a unique (LinkshellId, Name) index. An unnamed default group would therefore
// be undeletable-by-accident only until the next save.
//
// Mirrors LinkshellRoleDefaults + LinkshellController.EnsureDefaultRolesAsync.
public static class DkpPoolDefaults
{
    public const string DefaultPoolName = "Default";

    public static DkpPool BuildDefaultPool(int linkshellId) => new()
    {
        LinkshellId = linkshellId,
        Name = DefaultPoolName,
        SortOrder = 0,
        IsDefault = true,
        Accent = DkpPoolAccents.Default,
        CreatedAtUtc = DateTime.UtcNow,
    };
}

// Guarantees a linkshell has a default pool. Called from the linkshell-create paths AND lazily
// from DkpPoolResolver, so no creation path — present or future — can leave a linkshell
// poolless. Idempotent: the partial unique index on (LinkshellId) WHERE IsDefault means a race
// loses at the DB and we just re-read.
public sealed class DkpPoolProvisioner
{
    private readonly ApplicationDbContext _db;

    public DkpPoolProvisioner(ApplicationDbContext db)
    {
        _db = db;
    }

    // Returns the linkshell's default pool, creating it if this is the first time we've looked.
    public async Task<DkpPool> EnsureDefaultPoolAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var existing = await _db.DkpPools
            .FirstOrDefaultAsync(pool => pool.LinkshellId == linkshellId && pool.IsDefault, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var created = DkpPoolDefaults.BuildDefaultPool(linkshellId);
        _db.DkpPools.Add(created);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return created;
        }
        catch (DbUpdateException)
        {
            // Another request created it first — the unique index did its job. Drop our attempt
            // and take theirs.
            _db.Entry(created).State = EntityState.Detached;
            return await _db.DkpPools
                .FirstAsync(pool => pool.LinkshellId == linkshellId && pool.IsDefault, cancellationToken);
        }
    }
}
