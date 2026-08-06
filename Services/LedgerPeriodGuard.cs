using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Thrown when something tries to record gil into a stretch of time a leader has locked.
public sealed class TreasuryPeriodLockedException : Exception
{
    public TreasuryPeriodLockedException(DateTime lockedThroughUtc)
        : base("Entries on or before the locked date cannot be changed.")
    {
        LockedThroughUtc = lockedThroughUtc;
    }

    public DateTime LockedThroughUtc { get; }
}

// Enforces the treasury lock.
//
// Called from INSIDE TreasuryJournalWriter rather than from controllers, deliberately: a guard that
// each endpoint has to remember to call is a guard the next endpoint forgets. There is no path to
// the journal that does not pass through the writer, so there is no path that skips this.
//
// Absence of a LedgerPeriods row — or a null LockedThroughUtc — means nothing is locked, mirroring
// DkpLedgerEntry.DkpPoolId == null meaning the default pool. No backfill, and a linkshell that never
// touches the feature is never locked by it.
public sealed class LedgerPeriodGuard
{
    private readonly ApplicationDbContext _db;

    // Cached for the request: an auction close writes several entries and would otherwise re-read the
    // same row once per item.
    private readonly Dictionary<int, DateTime?> _lockedThrough = new();

    public LedgerPeriodGuard(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<DateTime?> GetLockedThroughAsync(int linkshellId, CancellationToken cancellationToken)
    {
        if (_lockedThrough.TryGetValue(linkshellId, out var cached))
        {
            return cached;
        }

        var period = await _db.LedgerPeriods
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.LinkshellId == linkshellId, cancellationToken);
        var lockedThrough = period?.LockedThroughUtc;
        _lockedThrough[linkshellId] = lockedThrough;
        return lockedThrough;
    }

    public async Task<bool> IsLockedAsync(int linkshellId, DateTime transactionDateUtc, CancellationToken cancellationToken)
    {
        var lockedThrough = await GetLockedThroughAsync(linkshellId, cancellationToken);
        return lockedThrough.HasValue && transactionDateUtc <= lockedThrough.Value;
    }

    public async Task EnsureOpenAsync(int linkshellId, DateTime transactionDateUtc, CancellationToken cancellationToken)
    {
        var lockedThrough = await GetLockedThroughAsync(linkshellId, cancellationToken);
        if (lockedThrough.HasValue && transactionDateUtc <= lockedThrough.Value)
        {
            throw new TreasuryPeriodLockedException(lockedThrough.Value);
        }
    }

    // Invalidate after a lock/unlock in the same request, so a leader who unlocks and immediately
    // records does not hit their own stale cache.
    public void Forget(int linkshellId) => _lockedThrough.Remove(linkshellId);
}
