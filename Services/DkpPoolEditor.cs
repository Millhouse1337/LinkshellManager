using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// A desired pool from a save request (Activity or web). Id is null for a new pool. EventTypes is
// the complete set assigned to it — the save is a full replace, so anything not listed here is
// unassigned and falls back to the default pool.
public sealed record DkpPoolEdit(
    int? Id,
    string? Name,
    bool IsDefault,
    int SortOrder,
    string? Accent,
    IReadOnlyList<string> EventTypes);

// What a save would do, shown to the officer before they commit. Deliberately a SUMMARY, not a
// per-member before/after table: pool balances are derived, so re-partitioning the ledger cannot
// change anyone's total — that is a proof, not a hope, and a two-band table of unchanged totals
// would only imply otherwise. What IS worth showing is where the DKP moves and which pools end up
// unable to pay for the loot they're charged for.
public sealed record DkpPoolRemapPreview(
    IReadOnlyList<DkpPoolRemapMove> Moves,
    int AffectedLedgerRows,
    int AffectedMembers,
    IReadOnlyList<string> Warnings);

public sealed record DkpPoolRemapMove(string EventType, string FromPool, string ToPool, double EarnedTotal, int LedgerRows);

// Shared save logic for a linkshell's DKP pools: a full replace with validation, plus the ledger
// re-stamp that IS the "recompute history" behaviour. Used by both the Activity API and the web
// Customize page — modelled on ChannelRouteEditor.
public sealed class DkpPoolEditor
{
    private readonly ApplicationDbContext _db;
    private readonly DkpPoolResolver _resolver;
    private readonly DkpPoolEventTypeCatalog _catalog;
    private readonly DkpSheetPostQueue _dkpSheetQueue;

    public DkpPoolEditor(
        ApplicationDbContext db,
        DkpPoolResolver resolver,
        DkpPoolEventTypeCatalog catalog,
        DkpSheetPostQueue dkpSheetQueue)
    {
        _db = db;
        _resolver = resolver;
        _catalog = catalog;
        _dkpSheetQueue = dkpSheetQueue;
    }

    // ---- validation ---------------------------------------------------------------

    // Returns null when the edits are savable, or a human-readable error.
    private static string? Validate(IReadOnlyList<DkpPoolEdit> edits)
    {
        var named = edits.Where(edit => !string.IsNullOrWhiteSpace(edit.Name)).ToList();
        if (named.Count == 0)
        {
            return "Keep at least one DKP pool.";
        }
        if (named.Any(edit => edit.Name!.Trim().Length > 64))
        {
            return "Pool names are limited to 64 characters.";
        }

        var duplicateName = named
            .GroupBy(edit => edit.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateName is not null)
        {
            return $"Two pools are both called \"{duplicateName.Key}\" — give them different names.";
        }

        // The partition: an event type belongs to exactly one pool. The UI binds a <select> per
        // event type so this is unrepresentable there, but the API is open to anyone.
        var duplicateType = named
            .SelectMany(edit => edit.EventTypes.Select(type => new { edit, type }))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.type))
            .GroupBy(pair => DkpPoolEventType.Normalize(pair.type), StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateType is not null)
        {
            var name = duplicateType.First().type.Trim();
            return $"\"{name}\" is assigned to more than one pool. Each event type can only earn into one pool.";
        }

        return null;
    }

    // ---- preview ------------------------------------------------------------------

    public async Task<DkpPoolRemapPreview?> PreviewAsync(
        int linkshellId, IReadOnlyList<DkpPoolEdit> edits, CancellationToken cancellationToken)
    {
        if (Validate(edits) is not null)
        {
            return null;
        }

        var map = await _resolver.GetMapAsync(linkshellId, cancellationToken);
        var catalog = await _catalog.LoadAsync(linkshellId, cancellationToken);
        var earnedByType = catalog.ToDictionary(
            option => DkpPoolEventType.Normalize(option.Key), option => option.EarnedTotal, StringComparer.Ordinal);

        var named = edits.Where(edit => !string.IsNullOrWhiteSpace(edit.Name)).ToList();
        var defaultEdit = named.FirstOrDefault(edit => edit.IsDefault) ?? named[0];

        // Where each event type lands AFTER the save, by pool NAME (new pools have no id yet).
        var targetNameByType = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var edit in named)
        {
            foreach (var type in edit.EventTypes.Where(type => !string.IsNullOrWhiteSpace(type)))
            {
                targetNameByType[DkpPoolEventType.Normalize(type)] = edit.Name!.Trim();
            }
        }

        var rowsByType = await _db.DkpLedgerEntries
            .Where(entry => entry.LinkshellId == linkshellId && !entry.DkpPoolPinned && entry.EventType != null)
            .GroupBy(entry => entry.EventType!)
            .Select(group => new
            {
                EventType = group.Key,
                Rows = group.Count(),
                Members = group.Select(entry => entry.AppUserId).Distinct().Count(),
            })
            .ToListAsync(cancellationToken);

        var moves = new List<DkpPoolRemapMove>();
        var affectedRows = 0;
        var affectedMembers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rowsByType)
        {
            var key = DkpPoolEventType.Normalize(row.EventType);
            var currentPoolName = map.NameFor(map.Resolve(row.EventType));
            var targetPoolName = targetNameByType.GetValueOrDefault(key, defaultEdit.Name!.Trim());
            if (string.Equals(currentPoolName, targetPoolName, StringComparison.Ordinal))
            {
                continue;
            }
            moves.Add(new DkpPoolRemapMove(
                row.EventType, currentPoolName, targetPoolName, earnedByType.GetValueOrDefault(key), row.Rows));
            affectedRows += row.Rows;
        }

        // Members touched by the moves — one query, not one per type.
        if (moves.Count > 0)
        {
            var movedTypes = moves.Select(move => move.EventType).ToList();
            var members = await _db.DkpLedgerEntries
                .Where(entry => entry.LinkshellId == linkshellId
                    && !entry.DkpPoolPinned
                    && entry.EventType != null
                    && movedTypes.Contains(entry.EventType)
                    && entry.AppUserId != null)
                .Select(entry => entry.AppUserId!)
                .Distinct()
                .ToListAsync(cancellationToken);
            foreach (var member in members) { affectedMembers.Add(member); }
        }

        var warnings = await BuildWarningsAsync(linkshellId, named, defaultEdit, catalog, targetNameByType, cancellationToken);
        return new DkpPoolRemapPreview(moves, affectedRows, affectedMembers.Count, warnings);
    }

    // The two configurations that actually hurt, both of which look perfectly reasonable when you
    // set them up.
    private async Task<List<string>> BuildWarningsAsync(
        int linkshellId,
        IReadOnlyList<DkpPoolEdit> named,
        DkpPoolEdit defaultEdit,
        IReadOnlyList<DkpPoolEventTypeOption> catalog,
        IReadOnlyDictionary<string, string> targetNameByType,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (named.Count <= 1)
        {
            return warnings;
        }

        var earnedByType = catalog.ToDictionary(
            option => DkpPoolEventType.Normalize(option.Key), option => option.EarnedTotal, StringComparer.Ordinal);

        // Which pool would earn nothing? Sum the lifetime earnings of the event types mapped to it.
        double EarnedForPool(DkpPoolEdit pool)
        {
            var explicitTypes = pool.EventTypes
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Sum(type => earnedByType.GetValueOrDefault(DkpPoolEventType.Normalize(type)));
            if (!pool.IsDefault)
            {
                return explicitTypes;
            }
            // The default pool also catches everything unmapped.
            var unmapped = catalog
                .Where(option => !targetNameByType.ContainsKey(DkpPoolEventType.Normalize(option.Key)))
                .Sum(option => option.EarnedTotal);
            return explicitTypes + unmapped;
        }

        // 1. THE footgun. The natural first config is Endgame = {Sky, Sea, Dynamis} with HNM left
        //    behind in Main — but HNM boards force DkpPerHour = 0, so Main earns NOTHING, while ToD
        //    loot is still charged to it. Every ToD award then fails the affordability guard.
        var hnmPoolName = targetNameByType.GetValueOrDefault(DkpPoolEventType.Normalize("HNM"), defaultEdit.Name!.Trim());
        var hnmPool = named.FirstOrDefault(pool => string.Equals(pool.Name!.Trim(), hnmPoolName, StringComparison.Ordinal));
        if (hnmPool is not null && EarnedForPool(hnmPool) <= 0)
        {
            warnings.Add(
                $"ToD/HNM loot is paid from \"{hnmPoolName}\" (the pool HNM maps to), but no event type in "
                + $"\"{hnmPoolName}\" has ever earned DKP. Members won't be able to afford ToD loot. "
                + "Map HNM into a pool that earns.");
        }

        // 2. Any live auction drawing from a pool nobody has DKP in.
        var liveAuctionPools = await _db.Auctions
            .Where(auction => auction.LinkshellId == linkshellId && auction.DkpPoolId != null)
            .Select(auction => auction.DkpPoolId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var poolId in liveAuctionPools)
        {
            var edit = named.FirstOrDefault(pool => pool.Id == poolId);
            if (edit is not null && EarnedForPool(edit) <= 0)
            {
                warnings.Add(
                    $"An open auction draws from \"{edit.Name!.Trim()}\", but no event type in that pool earns DKP — "
                    + "nobody will be able to bid.");
            }
        }

        // 3. Event types that have earned DKP but aren't mapped anywhere — they'll keep pouring into
        //    the default pool. Usually a free-text "Other" type nobody remembered to map.
        var unmappedEarners = catalog
            .Where(option => option.EarnedTotal > 0
                && !targetNameByType.ContainsKey(DkpPoolEventType.Normalize(option.Key)))
            .Select(option => option.Key)
            .ToList();
        if (unmappedEarners.Count > 0)
        {
            warnings.Add(
                $"Not assigned to a pool, so they earn into \"{defaultEdit.Name!.Trim()}\": "
                + string.Join(", ", unmappedEarners) + ".");
        }

        return warnings;
    }

    // ---- save ---------------------------------------------------------------------

    // Persists the desired pools. Returns null on success, or a human-readable error.
    public async Task<string?> SaveAsync(
        int linkshellId, IReadOnlyList<DkpPoolEdit> edits, CancellationToken cancellationToken)
    {
        var error = Validate(edits);
        if (error is not null)
        {
            return error;
        }

        var named = edits.Where(edit => !string.IsNullOrWhiteSpace(edit.Name)).ToList();
        var existing = await _db.DkpPools
            .Where(pool => pool.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        // Exactly one default. If the request didn't nominate one (or nominated several), the first
        // pool wins — the officer always ends up with a valid partition rather than an error.
        var defaultEdit = named.FirstOrDefault(edit => edit.IsDefault) ?? named[0];

        // A pool being dropped can't be deleted out from under a live auction: its bidders were
        // validated against THAT pool's balance and would be debited from a different one at close.
        var keptIds = named.Where(edit => edit.Id is int).Select(edit => edit.Id!.Value).ToHashSet();
        var dropped = existing.Where(pool => !keptIds.Contains(pool.Id)).ToList();
        if (dropped.Count > 0)
        {
            var droppedIds = dropped.Select(pool => pool.Id).ToList();
            var blocking = await _db.Auctions
                .Where(auction => auction.LinkshellId == linkshellId
                    && auction.DkpPoolId != null
                    && droppedIds.Contains(auction.DkpPoolId!.Value))
                .CountAsync(cancellationToken);
            if (blocking > 0)
            {
                var names = string.Join(", ", dropped.Select(pool => $"\"{pool.Name}\""));
                return $"Close or cancel the {blocking} open auction(s) drawing from {names} before removing "
                    + (dropped.Count == 1 ? "that pool." : "those pools.");
            }
        }

        // ---- upsert the pools
        DkpPool? defaultPool = null;
        var order = 0;
        var poolByEdit = new Dictionary<DkpPoolEdit, DkpPool>();
        foreach (var edit in named)
        {
            var pool = edit.Id is int id ? existing.FirstOrDefault(item => item.Id == id) : null;
            if (pool is null)
            {
                pool = new DkpPool
                {
                    LinkshellId = linkshellId,
                    CreatedAtUtc = DateTime.UtcNow,
                };
                _db.DkpPools.Add(pool);
            }
            pool.Name = edit.Name!.Trim();
            pool.SortOrder = order++;
            pool.Accent = DkpPoolAccents.Resolve(edit.Accent);
            // Clear every IsDefault first; the winner is set after the loop. Two pools briefly
            // holding IsDefault=true in the ChangeTracker would trip the partial unique index.
            pool.IsDefault = false;
            poolByEdit[edit] = pool;
            if (ReferenceEquals(edit, defaultEdit))
            {
                defaultPool = pool;
            }
        }
        defaultPool!.IsDefault = true;

        // ---- merge away the dropped pools
        // Their PINNED references (an auction's history, ToD loot, adjustments, imports) can't just
        // be orphaned — the ledger row still has to say which wallet it came out of. Repoint them at
        // the default pool; the derived balances then simply re-derive.
        if (dropped.Count > 0)
        {
            // Save first so `defaultPool` has a real Id to repoint at.
            await _db.SaveChangesAsync(cancellationToken);

            var droppedIds = dropped.Select(pool => pool.Id).ToList();
            await _db.DkpLedgerEntries
                .Where(entry => entry.LinkshellId == linkshellId
                    && entry.DkpPoolId != null
                    && droppedIds.Contains(entry.DkpPoolId!.Value))
                .ExecuteUpdateAsync(setter => setter.SetProperty(entry => entry.DkpPoolId, defaultPool.Id), cancellationToken);
            await _db.AuctionHistories
                .Where(item => item.LinkshellId == linkshellId
                    && item.DkpPoolId != null
                    && droppedIds.Contains(item.DkpPoolId!.Value))
                .ExecuteUpdateAsync(setter => setter.SetProperty(item => item.DkpPoolId, defaultPool.Id), cancellationToken);
            await _db.TodLootDetails
                .Where(item => item.DkpPoolId != null && droppedIds.Contains(item.DkpPoolId!.Value))
                .ExecuteUpdateAsync(setter => setter.SetProperty(item => item.DkpPoolId, defaultPool.Id), cancellationToken);

            foreach (var pool in dropped)
            {
                _db.DkpPools.Remove(pool);
            }
        }

        // ---- replace the event-type mapping
        var oldMappings = await _db.DkpPoolEventTypes
            .Where(mapping => mapping.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);
        _db.DkpPoolEventTypes.RemoveRange(oldMappings);

        foreach (var edit in named)
        {
            var pool = poolByEdit[edit];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in edit.EventTypes)
            {
                var value = type?.Trim();
                if (string.IsNullOrEmpty(value) || value.Length > 256 || !seen.Add(DkpPoolEventType.Normalize(value)))
                {
                    continue;
                }
                _db.DkpPoolEventTypes.Add(new DkpPoolEventType
                {
                    LinkshellId = linkshellId,
                    DkpPool = pool,
                    EventType = value,
                    NormalizedEventType = DkpPoolEventType.Normalize(value),
                });
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        _resolver.Invalidate(linkshellId);

        // ---- THE RE-STAMP: this is the "recompute history" behaviour, in one statement.
        //
        // An unpinned row's DkpPoolId is only ever a cache of "which pool does this row's EventType
        // map to". Recomputing balances after a remap therefore doesn't mean recomputing anything —
        // it means refreshing that cache, and letting the derivation fall out. Pinned rows (auction
        // spends, ToD loot, adjustments, imports) are never touched: an officer remapping Sea must
        // not retroactively relocate a historical auction purchase.
        //
        // Bounded to one linkshell and run inside the officer's own request, never at startup.
                await _db.Database.ExecuteSqlRawAsync(
                        """
                        UPDATE "DkpLedgerEntries" e
                        SET "DkpPoolId" = COALESCE(
                                (
                                        SELECT m."DkpPoolId"
                                        FROM "DkpPoolEventTypes" m
                                        WHERE m."LinkshellId" = e."LinkshellId"
                                            AND m."NormalizedEventType" = UPPER(TRIM(COALESCE(e."EventType", '')))
                                        LIMIT 1
                                ),
                                (
                                        SELECT d."Id"
                                        FROM "DkpPools" d
                                        WHERE d."LinkshellId" = e."LinkshellId"
                                            AND d."IsDefault"
                                        LIMIT 1
                                )
                        )
                        WHERE e."LinkshellId" = {0}
                            AND NOT e."DkpPoolPinned"
                            AND e."DkpPoolId" IS DISTINCT FROM COALESCE(
                                (
                                        SELECT m."DkpPoolId"
                                        FROM "DkpPoolEventTypes" m
                                        WHERE m."LinkshellId" = e."LinkshellId"
                                            AND m."NormalizedEventType" = UPPER(TRIM(COALESCE(e."EventType", '')))
                                        LIMIT 1
                                ),
                                (
                                        SELECT d."Id"
                                        FROM "DkpPools" d
                                        WHERE d."LinkshellId" = e."LinkshellId"
                                            AND d."IsDefault"
                                        LIMIT 1
                                )
                            );
                        """,
                        new object[] { linkshellId },
                        cancellationToken);

        // The sheet's per-pool columns just changed, and the bulk UPDATE above bypasses the
        // ChangeTracker that would normally notice.
        _dkpSheetQueue.Enqueue(linkshellId);
        return null;
    }
}
