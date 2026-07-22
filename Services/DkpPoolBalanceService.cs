using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One ledger row, reduced to what pool attribution needs.
public readonly record struct LedgerPoolRow(int Id, int? DkpPoolId, double Amount);

// THE reader of DKP pool balances. Balances are DERIVED from the ledger, never stored — there
// is no second cache to drift away from AppUserLinkshell.LinkshellDkp.
//
// The derivation, for one member:
//
//     pool[p]        = Σ Amount of their ledger rows stamped with pool p   (p != default)
//     pool[default]  = LinkshellDkp − Σ pool[p] for every non-default p
//
// The default pool absorbs the RESIDUAL, which is what makes
//
//     Σ pool[p] == LinkshellDkp
//
// true by construction — for ANY mapping, any watermark, and even for a member whose cached
// balance has drifted for unrelated historical reasons. Two consequences worth spelling out:
//
//   * Remapping event types re-partitions the same multiset of ledger amounts, so it can move
//     DKP BETWEEN pools but can never change anyone's total. That's the whole safety story
//     behind the "recompute history" behaviour.
//   * A row with a NULL DkpPoolId means "the default pool", so every row written before pools
//     existed is already correct and never needs backfilling.
//
// Rows at or below the member's DkpPoolLedgerFromId epoch are excluded from attribution (they
// collapse into the residual) — see AppUserLinkshell.DkpPoolLedgerFromId.
public sealed class DkpPoolBalanceService
{
    private readonly ApplicationDbContext _db;
    private readonly DkpPoolResolver _resolver;

    public DkpPoolBalanceService(ApplicationDbContext db, DkpPoolResolver resolver)
    {
        _db = db;
        _resolver = resolver;
    }

    // The projection, as a pure function. Shared by the live read path and by DkpPoolEditor's
    // remap preview, so the preview is literally the code that will run.
    public static IReadOnlyDictionary<int, double> Project(
        double linkshellDkp,
        IEnumerable<LedgerPoolRow> rows,
        int epochLedgerId,
        int defaultPoolId,
        IReadOnlyList<int> allPoolIds)
    {
        var byPool = new Dictionary<int, double>();
        foreach (var poolId in allPoolIds)
        {
            byPool[poolId] = 0d;
        }
        byPool[defaultPoolId] = 0d;

        var nonDefaultSum = 0d;
        foreach (var row in rows)
        {
            if (row.Id <= epochLedgerId)
            {
                continue;
            }
            var poolId = row.DkpPoolId ?? defaultPoolId;
            if (poolId == defaultPoolId)
            {
                continue; // The default pool is the residual; it is not summed directly.
            }
            byPool[poolId] = byPool.GetValueOrDefault(poolId) + row.Amount;
            nonDefaultSum += row.Amount;
        }

        byPool[defaultPoolId] = linkshellDkp - nonDefaultSum;
        return byPool;
    }

    // AppUserId -> (pool id -> balance), for a whole linkshell in two queries. Used by the DKP
    // sheet, the rosters and the remap preview.
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<int, double>>> GetByAppUserAsync(
        int linkshellId, CancellationToken cancellationToken)
    {
        var map = await _resolver.GetMapAsync(linkshellId, cancellationToken);
        var poolIds = map.Pools.Select(pool => pool.Id).ToList();

        var members = await _db.AppUserLinkshells
            .AsNoTracking()
            .Where(member => member.LinkshellId == linkshellId && member.AppUserId != null)
            .Select(member => new
            {
                member.AppUserId,
                Dkp = member.LinkshellDkp ?? 0d,
                Epoch = member.DkpPoolLedgerFromId,
            })
            .ToListAsync(cancellationToken);

        var rows = await _db.DkpLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.LinkshellId == linkshellId && entry.AppUserId != null)
            .Select(entry => new { entry.AppUserId, entry.Id, entry.DkpPoolId, entry.Amount })
            .ToListAsync(cancellationToken);

        var rowsByAppUser = rows
            .GroupBy(row => row.AppUserId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => new LedgerPoolRow(row.Id, row.DkpPoolId, row.Amount)).ToList(),
                StringComparer.Ordinal);

        var result = new Dictionary<string, IReadOnlyDictionary<int, double>>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            // Two memberships can share an AppUserId (there's no unique index on
            // (AppUserId, LinkshellId)). The first one wins the ledger, mirroring the
            // GroupBy(AppUserId).First() dedup that event close already does; the rest fall back
            // to their whole balance sitting in the default pool via the residual.
            if (result.ContainsKey(member.AppUserId!))
            {
                continue;
            }
            var memberRows = rowsByAppUser.GetValueOrDefault(member.AppUserId!) ?? new List<LedgerPoolRow>();
            result[member.AppUserId!] = Project(member.Dkp, memberRows, member.Epoch, map.DefaultPoolId, poolIds);
        }
        return result;
    }

    // One member, one pool. Reads committed state only — DkpLedgerWriter layers this request's
    // staged (not-yet-saved) rows on top, so mid-transaction callers see their own writes.
    public async Task<double> GetCommittedAsync(
        int linkshellId, string appUserId, int poolId, CancellationToken cancellationToken)
    {
        var map = await _resolver.GetMapAsync(linkshellId, cancellationToken);
        var member = await _db.AppUserLinkshells
            .AsNoTracking()
            .Where(m => m.LinkshellId == linkshellId && m.AppUserId == appUserId)
            .Select(m => new { Dkp = m.LinkshellDkp ?? 0d, Epoch = m.DkpPoolLedgerFromId })
            .FirstOrDefaultAsync(cancellationToken);
        if (member is null)
        {
            return 0d;
        }

        if (poolId != map.DefaultPoolId)
        {
            return await _db.DkpLedgerEntries
                .Where(entry => entry.LinkshellId == linkshellId
                    && entry.AppUserId == appUserId
                    && entry.DkpPoolId == poolId
                    && entry.Id > member.Epoch)
                .SumAsync(entry => entry.Amount, cancellationToken);
        }

        // The default pool is the residual: everything not claimed by another pool.
        var nonDefault = await _db.DkpLedgerEntries
            .Where(entry => entry.LinkshellId == linkshellId
                && entry.AppUserId == appUserId
                && entry.DkpPoolId != null
                && entry.DkpPoolId != map.DefaultPoolId
                && entry.Id > member.Epoch)
            .SumAsync(entry => entry.Amount, cancellationToken);
        return member.Dkp - nonDefault;
    }
}
