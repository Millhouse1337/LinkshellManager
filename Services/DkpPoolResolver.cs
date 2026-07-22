using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// A linkshell's event-type -> pool partition, resolved once and reused. Event.EventType is a
// free-form string (the web event form has an "Other" free-text field), so Resolve has to cope
// with null, unknown and arbitrarily-cased values — all of which land in the default pool.
public sealed record DkpPoolMap(
    int LinkshellId,
    int DefaultPoolId,
    IReadOnlyDictionary<string, int> PoolIdByNormalizedEventType,
    IReadOnlyList<DkpPool> Pools)
{
    public int Resolve(string? eventType)
    {
        var normalized = DkpPoolEventType.Normalize(eventType);
        if (normalized.Length == 0)
        {
            return DefaultPoolId;
        }
        return PoolIdByNormalizedEventType.TryGetValue(normalized, out var poolId) ? poolId : DefaultPoolId;
    }

    public DkpPool? PoolById(int? poolId) =>
        poolId is null ? null : Pools.FirstOrDefault(pool => pool.Id == poolId.Value);

    // The display name for a pool id, falling back to the default pool (which is what a null
    // DkpPoolId means everywhere).
    public string NameFor(int? poolId) =>
        (PoolById(poolId) ?? PoolById(DefaultPoolId))?.Name ?? DkpPoolDefaults.DefaultPoolName;

    // True when this linkshell has actually customized its pools. Every new selector, column and
    // tag hides itself when this is false, which is what makes the feature invisible until it's
    // used.
    public bool HasMultiplePools => Pools.Count > 1;
}

// Loads (and memoizes, per request) a linkshell's pool map. Scoped, because event close resolves
// the map once and then reads it inside the participant loop — an event has ONE event type, so
// there is never a per-member query.
public sealed class DkpPoolResolver
{
    private readonly ApplicationDbContext _db;
    private readonly DkpPoolProvisioner _provisioner;
    private readonly Dictionary<int, DkpPoolMap> _cache = new();

    public DkpPoolResolver(ApplicationDbContext db, DkpPoolProvisioner provisioner)
    {
        _db = db;
        _provisioner = provisioner;
    }

    public async Task<DkpPoolMap> GetMapAsync(int linkshellId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(linkshellId, out var cached))
        {
            return cached;
        }

        var pools = await _db.DkpPools
            .Include(pool => pool.EventTypes)
            .Where(pool => pool.LinkshellId == linkshellId)
            .OrderBy(pool => pool.SortOrder)
            .ThenBy(pool => pool.Id)
            .ToListAsync(cancellationToken);

        if (pools.Count == 0)
        {
            // A linkshell created before pools existed, or via a path that forgot to provision.
            // Fix it here rather than failing: this is the safety net that makes "every linkshell
            // has a default pool" an invariant instead of a hope.
            var created = await _provisioner.EnsureDefaultPoolAsync(linkshellId, cancellationToken);
            pools.Add(created);
        }

        var defaultPool = pools.FirstOrDefault(pool => pool.IsDefault) ?? pools[0];

        var mappings = await _db.DkpPoolEventTypes
            .Where(mapping => mapping.LinkshellId == linkshellId)
            .Select(mapping => new { mapping.NormalizedEventType, mapping.DkpPoolId })
            .ToListAsync(cancellationToken);

        var byEventType = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var mapping in mappings)
        {
            // Defensive: a mapping pointing at a pool that no longer exists resolves to default
            // rather than a dangling id.
            if (pools.Any(pool => pool.Id == mapping.DkpPoolId))
            {
                byEventType[mapping.NormalizedEventType] = mapping.DkpPoolId;
            }
        }

        var map = new DkpPoolMap(linkshellId, defaultPool.Id, byEventType, pools);
        _cache[linkshellId] = map;
        return map;
    }

    // Convenience for the common "which pool does this event pay into / out of" question.
    public async Task<int> ResolveAsync(int linkshellId, string? eventType, CancellationToken cancellationToken)
        => (await GetMapAsync(linkshellId, cancellationToken)).Resolve(eventType);

    // Drop the memo for a linkshell after its pools change inside the same request (DkpPoolEditor).
    public void Invalidate(int linkshellId) => _cache.Remove(linkshellId);
}
