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
    /// <summary>Twin of ChartLedgerRow.CreditedPercent, including the never-divide-by-zero rule.</summary>
    public int HavePercent => TotalColumns == 0 ? 0 : (int)Math.Round(100d * HaveCount / TotalColumns);
}

/// <summary>The whole grid: column order plus rows.</summary>
public sealed record ChartKeyItemGrid(
    IReadOnlyList<ChartKeyItemColumn> Columns,
    IReadOnlyList<ChartKeyItemGridRow> Rows);

/// <summary>
/// THE reader and writer of per-member key item progress.
///
/// Same arrangement as ChartBoardService and ChartWishlistService: both controllers are thin
/// callers, so a card badge on the website and the same badge in the Activity are one derivation.
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
    /// ROWS ARE ROSTER-DRIVEN, and a stored row for a membership no longer on the roster is ignored.
    /// Deliberately unlike ChartBoardService.BuildLedger, which keeps departed farmers: farming
    /// credit is a historical fact worth preserving, while "does this person have the key item" is
    /// only a question about people who are here. Those orphan rows are harmless - there is no FK to
    /// delete them, by the second-cascade-path design on the model - and re-adding the same person
    /// restores what they had.
    /// </summary>
    public static ChartKeyItemGrid BuildGrid(
        ChartBoard board,
        IReadOnlyList<ChartMemberKeyItem> rows,
        IReadOnlyList<ChartRosterEntry> roster)
    {
        // Membership ids that hold each key item. Keyed by the CATALOG spelling so a row stored in
        // another case still lands in its column.
        var holders = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in board.KeyItems)
        {
            holders[item.Name] = new HashSet<int>();
        }

        foreach (var row in rows)
        {
            if (holders.TryGetValue(row.KeyItemName.Trim(), out var set))
            {
                set.Add(row.MembershipId);
            }
        }

        // De-duplicated the same way BuildLedger does: one row per person, first sighting wins.
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
                    .Where(member => !set.Contains(member.MembershipId))
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
                    .Select(item => holders[item.Name].Contains(member.MembershipId))
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
    /// Static and shared for the same reason CanEditRequest is - both surfaces call this one copy,
    /// so neither can end up more permissive than the other by having written its own.
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

        var characterName = await _db.AppUserLinkshells
            .AsNoTracking()
            .Where(member => member.LinkshellId == linkshellId && member.Id == membershipId)
            .Select(member => member.CharacterName)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(characterName))
        {
            return "That member is not in this linkshell.";
        }

        var existing = await _db.ChartMemberKeyItems
            .FirstOrDefaultAsync(
                row => row.LinkshellId == linkshellId
                    && row.Board == catalog.Key
                    && row.KeyItemName == canonicalName
                    && row.MembershipId == membershipId,
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
            existing.CharacterName = characterName;
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
            MembershipId = membershipId,
            CharacterName = characterName,
            SetByAppUserId = actor.AppUserId,
            SetByCharacterName = actor.CharacterName,
            SetAt = DateTime.UtcNow,
        });

        return null;
    }
}
