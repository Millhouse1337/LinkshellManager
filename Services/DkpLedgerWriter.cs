using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// How a ledger row's pool is decided.
public readonly record struct DkpPoolRef(int? PinnedPoolId, string? DerivedFromEventType)
{
    // An explicit officer/system choice — an auction's pool, ToD loot, an adjustment, an import.
    // A remap must never move it.
    public static DkpPoolRef Pinned(int poolId) => new(poolId, null);

    // Follows the event type. The stamped pool is a cache of "what does this event type map to
    // right now", and DkpPoolEditor re-derives it whenever an officer remaps.
    public static DkpPoolRef Derived(string? eventType) => new(null, eventType);
}

// The denormalized display context every ledger row carries.
public sealed record DkpEntryContext(
    string? CharacterName = null,
    string? EventName = null,
    string? EventType = null,
    string? EventLocation = null,
    DateTime? EventStartTime = null,
    DateTime? EventEndTime = null,
    string? ItemName = null,
    string? Details = null,
    string? EditReason = null,
    EventHistory? EventHistory = null,
    int? EventHistoryId = null,
    int? SourceEventLootDetailId = null,
    int? SourceTodLootDetailId = null,
    AuctionHistory? SourceAuctionHistory = null,
    int? SourceAuctionHistoryId = null,
    int? SourceWindowEventId = null,
    int? AuditRelatedLedgerEntryId = null,
    int? AttInputRowNumber = null);

// THE single place DKP moves.
//
// Before this existed, ~18 call sites each hand-rolled the same three lines: bump
// AppUserLinkshell.LinkshellDkp, allocate a Sequence (in one of four mutually inconsistent
// ways), and add a DkpLedgerEntry. That is fine until you need a fourth thing to happen on every
// write — like stamping the DKP pool — at which point 18 copies is 18 chances to drift.
//
// Everything here upholds INV-1: a LinkshellDkp move is always matched, amount for amount, by a
// ledger row in the same save. ApplicationDbContext asserts it in DEBUG. INV-1 is what lets pool
// balances be derived rather than cached (DkpPoolBalanceService).
//
// This service never calls SaveChanges — callers batch their own save, and event close has to
// stay a single transaction.
public sealed class DkpLedgerWriter
{
    private readonly ApplicationDbContext _db;
    private readonly DkpPoolResolver _resolver;
    private readonly ILogger<DkpLedgerWriter> _logger;

    // Next Sequence per (linkshell, account), lazily seeded from MAX(Sequence) + 1. Sequence only
    // ever tie-breaks rows that share an OccurredAt, so a per-member monotonic counter is both
    // correct and immune to the collisions the old hardcoded "Sequence = 1" sites produced.
    private readonly Dictionary<(int LinkshellId, string AppUserId), int> _nextSequence = new();

    // Committed non-default pool sums per member, cached for the request. Staged (not-yet-saved)
    // rows are tracked separately below, so caching the committed half stays correct as we write.
    private readonly Dictionary<(int LinkshellId, string AppUserId), Dictionary<int, double>> _committedNonDefault = new();

    // Ledger amounts staged by THIS request, per (member, pool). Lets a caller read a pool balance
    // that already reflects rows it added moments ago — event close relies on this so the second
    // Hybrid item in an event sees the first item's debit.
    private readonly Dictionary<(int LinkshellId, string AppUserId, int PoolId), double> _stagedByPool = new();

    public DkpLedgerWriter(ApplicationDbContext db, DkpPoolResolver resolver, ILogger<DkpLedgerWriter> logger)
    {
        _db = db;
        _resolver = resolver;
        _logger = logger;
    }

    // Append one ledger row and move the member's balance by the same amount.
    // amount > 0 = earned, amount < 0 = spent.
    public async Task<DkpLedgerEntry?> AppendAsync(
        AppUserLinkshell member,
        string entryType,
        double amount,
        DateTime occurredAtUtc,
        DkpPoolRef pool,
        DkpEntryContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(member.AppUserId))
        {
            // Account-less (outside-signup) members have no ledger identity and no balance.
            return null;
        }

        var (poolId, pinned) = await ResolvePoolAsync(member.LinkshellId, pool, cancellationToken);

        var entry = new DkpLedgerEntry
        {
            AppUserId = member.AppUserId,
            LinkshellId = member.LinkshellId,
            EntryType = entryType,
            Amount = amount,
            Sequence = await NextSequenceAsync(member.LinkshellId, member.AppUserId!, cancellationToken),
            OccurredAt = occurredAtUtc,
            DkpPoolId = poolId,
            DkpPoolPinned = pinned,
            CharacterName = context.CharacterName ?? member.CharacterName,
            EventName = context.EventName,
            EventType = context.EventType ?? pool.DerivedFromEventType,
            EventLocation = context.EventLocation,
            EventStartTime = context.EventStartTime,
            EventEndTime = context.EventEndTime,
            ItemName = context.ItemName,
            Details = context.Details,
            EditReason = context.EditReason,
            EventHistory = context.EventHistory,
            EventHistoryId = context.EventHistory is null ? context.EventHistoryId : null,
            SourceEventLootDetailId = context.SourceEventLootDetailId,
            SourceTodLootDetailId = context.SourceTodLootDetailId,
            SourceAuctionHistory = context.SourceAuctionHistory,
            SourceAuctionHistoryId = context.SourceAuctionHistory is null ? context.SourceAuctionHistoryId : null,
            SourceWindowEventId = context.SourceWindowEventId,
            AuditRelatedLedgerEntryId = context.AuditRelatedLedgerEntryId,
            AttInputRowNumber = context.AttInputRowNumber,
        };

        _db.DkpLedgerEntries.Add(entry);
        member.LinkshellDkp = (member.LinkshellDkp ?? 0d) + amount;
        StageDelta(member.LinkshellId, member.AppUserId!, poolId, amount);
        return entry;
    }

    // Change an existing row's amount in place, moving the balance by the delta. Used by the
    // closed-event editor and the window-event reconciler, which correct history rather than
    // appending compensating rows.
    //
    // member may be null: EventHistoryEditService mutates ledger rows for accounts whose
    // membership has since been removed. Then the row changes and the (non-existent) balance
    // doesn't — which is exactly what happened before, just now it's explicit.
    public void Amend(DkpLedgerEntry entry, double newAmount, string? newDetails, AppUserLinkshell? member)
    {
        var delta = newAmount - entry.Amount;
        entry.Amount = newAmount;
        if (newDetails is not null)
        {
            entry.Details = newDetails;
        }

        if (member is null || string.IsNullOrWhiteSpace(member.AppUserId))
        {
            if (Math.Abs(delta) > 1e-9)
            {
                _logger.LogWarning(
                    "Amended ledger entry {EntryId} by {Delta} DKP with no membership to credit — balance not moved.",
                    entry.Id, delta);
            }
            return;
        }

        member.LinkshellDkp = (member.LinkshellDkp ?? 0d) + delta;
        StageDelta(member.LinkshellId, member.AppUserId!, entry.DkpPoolId ?? 0, delta);
    }

    // Delete a row and reverse ITS OWN amount from the balance.
    //
    // Reversing entry.Amount (rather than whatever the caller thinks the row was worth) is what
    // makes this safe: the previous code subtracted a separately-tracked figure that could have
    // drifted from the row it was deleting.
    public void Remove(DkpLedgerEntry entry, AppUserLinkshell? member)
    {
        _db.DkpLedgerEntries.Remove(entry);

        if (member is null || string.IsNullOrWhiteSpace(member.AppUserId))
        {
            if (Math.Abs(entry.Amount) > 1e-9)
            {
                _logger.LogWarning(
                    "Removed ledger entry {EntryId} worth {Amount} DKP with no membership to debit — balance not moved.",
                    entry.Id, entry.Amount);
            }
            return;
        }

        member.LinkshellDkp = (member.LinkshellDkp ?? 0d) - entry.Amount;
        StageDelta(member.LinkshellId, member.AppUserId!, entry.DkpPoolId ?? 0, -entry.Amount);
    }

    // Re-stamp an unpinned row's cached pool after its event type changed (a closed-event retype,
    // a window-event entry-type edit). Pure: balances are derived, so there is nothing to move.
    public void Repoint(DkpLedgerEntry entry, int newPoolId)
    {
        if (entry.DkpPoolPinned)
        {
            return;
        }
        entry.DkpPoolId = newPoolId;
    }

    // A member's balance in one pool, including rows this request has staged but not yet saved.
    // This is the base for the Hybrid loot percentage and for the affordability guards.
    public async Task<double> GetPoolBalanceAsync(AppUserLinkshell member, int poolId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(member.AppUserId))
        {
            return member.LinkshellDkp ?? 0d;
        }

        var map = await _resolver.GetMapAsync(member.LinkshellId, cancellationToken);

        if (poolId != map.DefaultPoolId)
        {
            var committed = await GetCommittedNonDefaultAsync(member, cancellationToken);
            return committed.GetValueOrDefault(poolId)
                + _stagedByPool.GetValueOrDefault((member.LinkshellId, member.AppUserId!, poolId));
        }

        // The default pool holds the residual: the member's whole balance, minus whatever the
        // other pools have claimed. With a single pool that's LinkshellDkp exactly, and the query
        // below is skipped entirely — the no-pools case costs nothing.
        var nonDefaultTotal = 0d;
        if (map.HasMultiplePools)
        {
            var committed = await GetCommittedNonDefaultAsync(member, cancellationToken);
            foreach (var pool in map.Pools)
            {
                if (pool.Id == map.DefaultPoolId)
                {
                    continue;
                }
                nonDefaultTotal += committed.GetValueOrDefault(pool.Id)
                    + _stagedByPool.GetValueOrDefault((member.LinkshellId, member.AppUserId!, pool.Id));
            }
        }
        return (member.LinkshellDkp ?? 0d) - nonDefaultTotal;
    }

    // ---- internals ----------------------------------------------------------------

    private async Task<(int PoolId, bool Pinned)> ResolvePoolAsync(
        int linkshellId, DkpPoolRef pool, CancellationToken cancellationToken)
    {
        if (pool.PinnedPoolId is int pinnedId)
        {
            return (pinnedId, true);
        }
        var map = await _resolver.GetMapAsync(linkshellId, cancellationToken);
        return (map.Resolve(pool.DerivedFromEventType), false);
    }

    private async Task<int> NextSequenceAsync(int linkshellId, string appUserId, CancellationToken cancellationToken)
    {
        var key = (linkshellId, appUserId);
        if (!_nextSequence.TryGetValue(key, out var next))
        {
            var max = await _db.DkpLedgerEntries
                .Where(entry => entry.LinkshellId == linkshellId && entry.AppUserId == appUserId)
                .Select(entry => (int?)entry.Sequence)
                .MaxAsync(cancellationToken);
            next = (max ?? 0) + 1;
        }
        _nextSequence[key] = next + 1;
        return next;
    }

    private async Task<Dictionary<int, double>> GetCommittedNonDefaultAsync(
        AppUserLinkshell member, CancellationToken cancellationToken)
    {
        var key = (member.LinkshellId, member.AppUserId!);
        if (_committedNonDefault.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var map = await _resolver.GetMapAsync(member.LinkshellId, cancellationToken);
        var sums = new Dictionary<int, double>();
        if (map.HasMultiplePools)
        {
            var rows = await _db.DkpLedgerEntries
                .Where(entry => entry.LinkshellId == member.LinkshellId
                    && entry.AppUserId == member.AppUserId
                    && entry.DkpPoolId != null
                    && entry.DkpPoolId != map.DefaultPoolId
                    && entry.Id > member.DkpPoolLedgerFromId)
                .GroupBy(entry => entry.DkpPoolId!.Value)
                .Select(group => new { PoolId = group.Key, Sum = group.Sum(entry => entry.Amount) })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                sums[row.PoolId] = row.Sum;
            }
        }

        _committedNonDefault[key] = sums;
        return sums;
    }

    private void StageDelta(int linkshellId, string appUserId, int poolId, double delta)
    {
        if (poolId == 0)
        {
            return;
        }
        var key = (linkshellId, appUserId, poolId);
        _stagedByPool[key] = _stagedByPool.GetValueOrDefault(key) + delta;
    }
}
