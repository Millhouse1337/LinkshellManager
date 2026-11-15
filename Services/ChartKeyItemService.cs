using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

/// <summary>
/// One column of the key item grid: a catalog key item, and how the roster stands on it.
/// </summary>
/// <param name="Boss">The card it is earned on, or null for a board-level prerequisite. A null Boss
/// gets a column and no card badge.</param>
/// <param name="MissingCharacterNames">Exactly what the card drawer lists, in roster order. The
/// INVERSE of the stored rows, because the stored fact is "has it" and the useful question is the
/// other one.</param>
public sealed record ChartKeyItemColumn(
    string Name,
    string? Boss,
    string? Caption,
    int HaveCount,
    int TotalMembers,
    IReadOnlyList<string> MissingCharacterNames);

/// <summary>One member's row across every column, aligned to the column order.</summary>
public sealed record ChartKeyItemGridRow(
    int MembershipId,
    string CharacterName,
    string? Rank,
    IReadOnlyList<bool> Has,
    int HaveCount,
    int TotalColumns)
{
    /// <summary>0 when there are no columns: never a divide by zero.</summary>
    public int HavePercent => TotalColumns == 0 ? 0 : (int)Math.Round(100d * HaveCount / TotalColumns);
}

/// <summary>The whole grid: column order plus rows.</summary>
public sealed record ChartKeyItemGrid(
    IReadOnlyList<ChartKeyItemColumn> Columns,
    IReadOnlyList<ChartKeyItemGridRow> Rows);

/// <summary>
/// THE reader and writer of per-member key item progress.
///
/// Same arrangement as ChartBoardService and ChartWishlistService: the rules live here and the
/// controller is a thin caller.
/// </summary>
public sealed class ChartKeyItemService
{
    private readonly ApplicationDbContext _db;

    public ChartKeyItemService(ApplicationDbContext db)
    {
        _db = db;
    }

    // ---- the derivation (pure - no database, no clock) --------------------------

    /// <summary>
    /// Builds the key item grid from the stored rows and the roster.
    ///
    /// COLUMNS COME FROM THE CATALOG, in catalog order, never from the data. A key item nobody has
    /// yet still gets a column reading "0 of 14 have it", which is the whole point: the grid exists
    /// to show what is outstanding, and deriving its columns from what people already hold would
    /// hide exactly the rows that matter.
    ///
    /// ROWS ARE ROSTER-DRIVEN, and a stored row for an account no longer on the roster is ignored.
    /// Deliberately unlike the pop item credits, which keep departed farmers: farming
    /// credit is a historical fact worth preserving, while "does this person have the key item" is
    /// only a question about people who are here. Those rows are harmless, and the same person
    /// coming back restores what they had.
    ///
    /// A tick belongs to the member's ACCOUNT (ChartMemberKeyItem.AppUserId), not to the membership
    /// row, so a member whose membership was re-created still sees their ticks. A roster entry with
    /// no account can hold none.
    /// </summary>
    public static ChartKeyItemGrid BuildGrid(
        ChartBoard board,
        IReadOnlyList<ChartMemberKeyItem> rows,
        IReadOnlyList<ChartRosterEntry> roster)
    {
        // Accounts that hold each key item. Keyed by the CATALOG spelling so a row stored in
        // another case still lands in its column. Account ids compare exactly, as the database does.
        var holders = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in board.KeyItems)
        {
            holders[item.Name] = new HashSet<string>(StringComparer.Ordinal);
        }

        foreach (var row in rows)
        {
            if (row.AppUserId is not null && holders.TryGetValue(row.KeyItemName.Trim(), out var set))
            {
                set.Add(row.AppUserId);
            }
        }

        static bool Holds(HashSet<string> set, ChartRosterEntry member) =>
            member.AppUserId is not null && set.Contains(member.AppUserId);

        // De-duplicated by name: one row per person, first sighting wins.
        var members = new List<ChartRosterEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in roster)
        {
            if (seen.Add(member.CharacterName))
            {
                members.Add(member);
            }
        }

        var columns = board.KeyItems
            .Select(item =>
            {
                var set = holders[item.Name];
                var missing = members
                    .Where(member => !Holds(set, member))
                    .Select(member => member.CharacterName)
                    .ToList();

                return new ChartKeyItemColumn(
                    item.Name,
                    item.Boss,
                    item.Caption,
                    members.Count - missing.Count,
                    members.Count,
                    missing);
            })
            .ToList();

        var gridRows = members
            .Select(member =>
            {
                var has = board.KeyItems
                    .Select(item => Holds(holders[item.Name], member))
                    .ToList();

                return new ChartKeyItemGridRow(
                    member.MembershipId,
                    member.CharacterName,
                    member.Rank,
                    has,
                    has.Count(value => value),
                    has.Count);
            })
            .ToList();

        return new ChartKeyItemGrid(columns, gridRows);
    }

    /// <summary>
    /// THE second ownership rule: a member may tick their OWN cell, an officer may tick anybody's.
    ///
    /// Static and shared for the same reason CanEditRequest is - one copy, so no controller can end
    /// up more permissive by having written its own.
    ///
    /// A viewer with no membership id never matches, even against membership 0.
    /// </summary>
    public static bool CanSetKeyItemFor(int targetMembershipId, int? viewerMembershipId, bool canManage) =>
        canManage || (viewerMembershipId is { } mine && mine == targetMembershipId);

    // ---- database ---------------------------------------------------------------

    public async Task<List<ChartMemberKeyItem>> LoadAsync(
        int linkshellId, string board, CancellationToken cancellationToken) =>
        await _db.ChartMemberKeyItems
            .AsNoTracking()
            .Where(row => row.LinkshellId == linkshellId && row.Board == board)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Ticks or unticks one cell. Returns the error message, or null on success. Does not save.
    ///
    /// IDEMPOTENT in both directions: ticking something already held is a no-op rather than a second
    /// row, which is what the unique index on the table is there to make impossible anyway. A
    /// double-clicked checkbox is a UI slip, not two facts.
    ///
    /// Both boundaries are drawn here rather than in the callers. The key item name is canonicalised
    /// against the CLOSED catalog list, so a row can never land in a column the grid does not draw;
    /// and the membership is checked against THIS linkshell, the same boundary
    /// ChartBoardService.ResolveCreditsAsync draws, so a request cannot tick a cell for somebody
    /// else's member.
    /// </summary>
    public async Task<string?> SetAsync(
        int linkshellId,
        string? board,
        string? keyItemName,
        int membershipId,
        bool has,
        ChartBoardActor actor,
        CancellationToken cancellationToken)
    {
        var catalog = ChartBoardCatalog.Find(board);
        if (catalog is null || !catalog.AllowsKeyItems)
        {
            return "That board does not track key items.";
        }

        var canonicalName = ChartBoardCatalog.NormalizeKeyItemName(catalog.Key, keyItemName);
        if (canonicalName is null)
        {
            return "That is not a key item on this board.";
        }

        // The membership check, and whose tick it is: the grid reads names off the roster, so the
        // row stores none, but it does store the member's account (the grid matches by account).
        var member = await _db.AppUserLinkshells
            .AsNoTracking()
            .Where(link => link.LinkshellId == linkshellId && link.Id == membershipId)
            .Select(link => new { link.CharacterName, link.AppUserId })
            .FirstOrDefaultAsync(cancellationToken);

        if (member is null || string.IsNullOrWhiteSpace(member.CharacterName))
        {
            return "That member is not in this linkshell.";
        }
        if (string.IsNullOrWhiteSpace(member.AppUserId))
        {
            return "That member has no account yet, so their key items can't be tracked.";
        }

        // By account, so a tick made on an older membership of the same person is found too.
        var existing = await _db.ChartMemberKeyItems
            .FirstOrDefaultAsync(
                row => row.LinkshellId == linkshellId
                    && row.Board == catalog.Key
                    && row.KeyItemName == canonicalName
                    && row.AppUserId == member.AppUserId,
                cancellationToken);

        if (!has)
        {
            // Unticking DELETES. Presence is the fact - see ChartMemberKeyItem.
            if (existing is not null)
            {
                _db.ChartMemberKeyItems.Remove(existing);
            }
            return null;
        }

        if (existing is not null)
        {
            // Already held. Re-stamp the audit rather than insert: whoever ticked it most recently
            // is the useful answer, and a second row would violate the unique index anyway.
            existing.SetByAppUserId = actor.AppUserId;
            existing.SetByCharacterName = actor.CharacterName;
            existing.SetAt = DateTime.UtcNow;
            return null;
        }

        _db.ChartMemberKeyItems.Add(new ChartMemberKeyItem
        {
            LinkshellId = linkshellId,
            Board = catalog.Key,
            KeyItemName = canonicalName,
            // Whose tick it is: the ticked member's account (ticks are matched by account).
            AppUserId = member.AppUserId,
            SetByAppUserId = actor.AppUserId,
            SetByCharacterName = actor.CharacterName,
            SetAt = DateTime.UtcNow,
        });

        return null;
    }
}
