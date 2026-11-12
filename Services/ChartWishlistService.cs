using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

/// <summary>The two states a request can be in. Strings on the wire, like ChartCreditStatuses.</summary>
public static class ChartWishlistStatuses
{
    public const string Pending = "Pending";

    public const string Fulfilled = "Fulfilled";

    /// <summary>
    /// There is deliberately no Withdrawn. Withdrawing DELETES the row: a withdrawn request is a
    /// thing nobody reads again, and keeping one would mean every list query filters it out forever.
    /// An officer removing somebody else's request is the same operation, so one endpoint covers
    /// both.
    /// </summary>
    public static readonly IReadOnlyList<string> All = new[] { Pending, Fulfilled };
}

/// <summary>What a member typed, cleaned up. Bosses is empty for "anywhere on this board".</summary>
public sealed record ChartWishlistDraft(
    string Board, IReadOnlyList<string> Bosses, string ItemName, int Quantity, string? Notes);

/// <summary>One request as the app renders it.</summary>
/// <param name="CanWithdraw">Decided HERE, per viewer, so no view re-derives it. A template that
/// works out for itself whether to show a Withdraw button is a second copy of the ownership rule,
/// and two copies are exactly what lets one end up more permissive than the other.</param>
public sealed record ChartWishlistRow(
    int Id,
    string Board,
    // The cards it names, in board order. Empty = anywhere.
    IReadOnlyList<string> Bosses,
    string ItemName,
    int Quantity,
    string? Notes,
    string Status,
    int Priority,
    string RequestedByCharacterName,
    bool CanWithdraw,
    DateTime RequestedAt,
    DateTime? FulfilledAt,
    string? FulfilledByCharacterName);

/// <summary>A board's requests, plus the per-card counts its badges show.</summary>
/// <param name="PendingCountsByBoss">Case-insensitive. Requests tied to NO boss are excluded: they
/// belong to the board, and there is no card to badge.</param>
public sealed record ChartWishlistBoard(
    IReadOnlyList<ChartWishlistRow> Requests,
    IReadOnlyDictionary<string, int> PendingCountsByBoss,
    int PendingCount);

/// <summary>
/// THE reader and writer of the Charts wishlist.
///
/// Same arrangement as ChartBoardService: the rules live here and the controller is a thin caller.
/// That matters more here than anywhere else in Charts, because this is the first write path open
/// to a member who does NOT have CanManageCharts - see <see cref="CanEditRequest"/>.
/// </summary>
public sealed class ChartWishlistService
{
    private readonly ApplicationDbContext _db;

    public ChartWishlistService(ApplicationDbContext db)
    {
        _db = db;
    }

    // ---- the pure core (no database, no clock - this is the testable part) ------

    /// <summary>
    /// Cleans up what a member typed. Null when the board is unknown, the board offers no wishlist,
    /// the item has no name, or any non-blank boss is not a card on that board.
    ///
    /// Blank bosses are dropped, and none at all means "anywhere on this board": the option the
    /// form opens on and a real answer, not a missing one. The rest come back in the board's own
    /// order, each once, spelled as the catalog spells it.
    ///
    /// Unlike ChartBoardService.NormalizeDraft, this one DOES check the board's feature flag. The
    /// asymmetry is deliberate and is the whole point of that method's comment: pop items exist on
    /// boards that no longer take new ones, so refusing there would strand rows, whereas a wishlist
    /// request on a board with no wishlist has never existed and never will.
    /// </summary>
    public static ChartWishlistDraft? NormalizeDraft(
        string? board, IEnumerable<string?>? bosses, string? itemName, int quantity, string? notes)
    {
        var catalog = ChartBoardCatalog.Find(board);
        if (catalog is null || !catalog.AllowsWishlist)
        {
            return null;
        }

        var name = itemName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var boss in bosses ?? Array.Empty<string?>())
        {
            if (string.IsNullOrWhiteSpace(boss))
            {
                continue;
            }
            var card = catalog.Find(boss);
            if (card is null || card.Kind == ChartBossKinds.Tier)
            {
                // Named a card and got it wrong - or named a tier's pop item strip, which is not
                // somewhere loot comes from. The WHOLE request is refused rather than that zone
                // quietly dropped (or the request widened to "anywhere"): changing what somebody
                // asked for is worse than saying no.
                return null;
            }
            picked.Add(card.Name);
        }

        return new ChartWishlistDraft(
            catalog.Key,
            catalog.Bosses.Select(card => card.Name).Where(picked.Contains).ToList(),
            Truncate(name, 128)!,
            // Clamped to at least one. A request for zero of something is not a request, and the
            // check constraint refuses it anyway.
            Math.Max(1, quantity),
            Truncate(NullIfBlank(notes), 512));
    }

    /// <summary>
    /// THE ownership rule: a member may act on their OWN request, an officer on any.
    ///
    /// Static and pure so the service-level tests reach it, and so no controller can hold a
    /// private copy that drifts. A coarse check on one front-end and a named permission on the other
    /// is a privilege escalation available by picking a front-end - the bug
    /// GrantTreasuryToOfficersWhoUsedIt exists to document, and the reason the Charts API checks
    /// CanManageCharts rather than a rank.
    ///
    /// A null or empty viewer id NEVER matches, even against a row whose requester id is also null.
    /// An unsynced member has no account behind the name, and "nobody" must not own "nobody else's".
    /// </summary>
    public static bool CanEditRequest(ChartWishlistRequest row, string? viewerAppUserId, bool canManage) =>
        canManage
        || (!string.IsNullOrEmpty(viewerAppUserId)
            && string.Equals(row.RequestedByAppUserId, viewerAppUserId, StringComparison.Ordinal));

    /// <summary>Canonical status, or null for anything that is not one.</summary>
    public static string? NormalizeStatus(string? status) =>
        ChartWishlistStatuses.All.FirstOrDefault(known =>
            string.Equals(known, status?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Projects stored rows into what the app renders, badge counts included.
    ///
    /// Counts are folded out of the SAME list the page lists, never queried separately: a badge
    /// saying 3 above a list showing 2 is the one bug this shape makes impossible.
    /// </summary>
    public static ChartWishlistBoard BuildWishlist(
        ChartBoard board,
        IReadOnlyList<ChartWishlistRequest> rows,
        string? viewerAppUserId,
        bool canManage)
    {
        var requests = rows
            .Select(row => new ChartWishlistRow(
                row.Id,
                row.Board,
                ZonesInBoardOrder(board, row),
                row.ItemName,
                row.Quantity,
                row.Notes,
                row.Status,
                row.Priority,
                row.RequestedByCharacterName,
                CanEditRequest(row, viewerAppUserId, canManage),
                row.RequestedAt,
                row.FulfilledAt,
                row.FulfilledByCharacterName))
            .ToList();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            // Pending only: a fulfilled request is settled, and a card still advertising it would
            // read as outstanding work. A request tied to no zone badges no card - it belongs to the
            // board, which is exactly what leaving the zone blank said.
            if (!IsPending(row))
            {
                continue;
            }

            // Every card it names counts it ONCE. Resolved through the catalog, so a badge lands on
            // the card whatever case the row is spelled in, and a zone this board no longer has
            // badges nothing.
            var cards = row.Zones
                .Select(zone => board.Find(zone.Boss))
                .Where(card => card is not null)
                .Select(card => card!.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                counts[card] = counts.TryGetValue(card, out var running) ? running + 1 : 1;
            }
        }

        return new ChartWishlistBoard(requests, counts, rows.Count(IsPending));
    }

    /// <summary>
    /// A request's zones in the order the board lists its cards, each spelled as the catalog spells
    /// it. A zone the board no longer has is kept, after the rest, as stored: the list still shows
    /// what was asked for even though no card badges it.
    /// </summary>
    private static IReadOnlyList<string> ZonesInBoardOrder(ChartBoard board, ChartWishlistRequest row)
    {
        var order = board.Bosses
            .Select((card, index) => (card.Name, index))
            .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        return row.Zones
            .Select(zone => board.Find(zone.Boss)?.Name ?? zone.Boss)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => order.TryGetValue(name, out var index) ? index : int.MaxValue)
            .ToList();
    }

    // ---- database ---------------------------------------------------------------

    /// <summary>
    /// Every request on a board, in the order the app shows them: pending first, then by the
    /// priority officers set, then oldest first.
    /// </summary>
    public async Task<List<ChartWishlistRequest>> LoadAsync(
        int linkshellId, string board, CancellationToken cancellationToken) =>
        await _db.ChartWishlistRequests
            .AsNoTracking()
            .Include(row => row.Zones)
            .Where(row => row.LinkshellId == linkshellId && row.Board == board)
            .OrderBy(row => row.Status == ChartWishlistStatuses.Pending ? 0 : 1)
            .ThenBy(row => row.Priority)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken);

    /// <summary>Next position on a board, so a new request lands at the bottom of the queue.</summary>
    public async Task<int> NextPriorityAsync(
        int linkshellId, string board, CancellationToken cancellationToken)
    {
        var highest = await _db.ChartWishlistRequests
            .Where(row => row.LinkshellId == linkshellId && row.Board == board)
            .MaxAsync(row => (int?)row.Priority, cancellationToken);
        return (highest ?? -1) + 1;
    }

    /// <summary>
    /// Reorders a board's queue set-wise: the caller sends the COMPLETE ordered id list and the
    /// positions are rewritten from it.
    ///
    /// An id belonging to another linkshell or another board refuses the WHOLE request rather than
    /// reordering the rest - the same boundary ChartBoardService.ResolveCreditsAsync draws, and for
    /// the same reason: a partial write here leaves a queue nobody asked for. Returns the error
    /// message, or null on success. Does not save.
    /// </summary>
    public async Task<string?> ReorderAsync(
        int linkshellId, string board, IReadOnlyList<int> orderedIds, CancellationToken cancellationToken)
    {
        var wanted = orderedIds.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return null;
        }

        var rows = await _db.ChartWishlistRequests
            .Where(row => row.LinkshellId == linkshellId && row.Board == board && wanted.Contains(row.Id))
            .ToListAsync(cancellationToken);

        if (rows.Count != wanted.Count)
        {
            return "One of those requests is not on this board.";
        }

        var byId = rows.ToDictionary(row => row.Id);
        for (var position = 0; position < wanted.Count; position++)
        {
            byId[wanted[position]].Priority = position;
        }

        return null;
    }

    private static bool IsPending(ChartWishlistRequest row) =>
        string.Equals(row.Status, ChartWishlistStatuses.Pending, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
