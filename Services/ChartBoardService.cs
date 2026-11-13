using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

/// <summary>One roster entry, reduced to what the boards need.</summary>
/// <param name="AltCharacterNames">This member's OWN other characters, off their profile — the same
/// two AppUser.AltCharacterName1/2 the alt picker elsewhere in the app offers. Empty for an unsynced
/// member, who has no account behind the name and so no alts on file.
///
/// Carried for the "Held by" list only: a pop item really does sit on somebody's alt, and it sits
/// there under the alt's OWN name. Farming credit is deliberately NOT offered per alt — credit is
/// attributed to a membership, and an alt is the same person.</param>
public sealed record ChartRosterEntry(
    int MembershipId,
    string? AppUserId,
    string CharacterName,
    string? Rank,
    IReadOnlyList<string> AltCharacterNames);

/// <summary>What an officer typed, before it is trusted.</summary>
public sealed record ChartPopItemDraft(
    string Board, string Boss, string ItemName, string? HeldByCharacterName,
    int Quantity, string? Notes)
{
    /// <summary>
    /// ChartItemKinds.Pop or Drop.
    ///
    /// An init property rather than an eighth positional member: every member of this record is a
    /// string or a string?, so a new positional one inserted anywhere but the very end would rebind
    /// silently at the single construction site with nothing worse than a nullable warning. Same
    /// hazard ChartBoss keeps warning about, and the same answer ChartBoard.Features gives.
    /// </summary>
    public string Kind { get; init; } = ChartItemKinds.Pop;
}

/// <summary>One credit as requested. Either a roster membership id, or just a name for an unsynced farmer.</summary>
public sealed record ChartCreditDraft(int? MembershipId, string? CharacterName);

/// <summary>
/// A resolved, trusted credit: a membership id has been checked against the linkshell and turned into
/// that member's name. Only the NAME is stored - farmers are matched by name, so a member who
/// re-syncs keeps their history.
/// </summary>
public sealed record ChartResolvedCredit(string CharacterName);

/// <summary>Who is performing a write, for the audit columns.</summary>
public readonly record struct ChartBoardActor(string? AppUserId, string? CharacterName);

/// <summary>
/// THE reader and writer of the Charts boards.
///
/// The rules live here rather than in the controller because the tests in this repo are
/// service-level, so anything worth pinning has to be reachable without spinning up a controller.
///
/// Board-agnostic throughout: Sky's five gods and Sea's eight Jailers differ only by what
/// ChartBoardCatalog says, never by a branch in here.
///
/// The central design decision: farming credit is stored ONLY per (pop item × person), and every
/// coarser view (the boss card's per-item farmer count) is derived from those rows on every read,
/// never stored, so two parts of the same page cannot disagree.
/// </summary>
public sealed class ChartBoardService
{
    private readonly ApplicationDbContext _db;

    public ChartBoardService(ApplicationDbContext db)
    {
        _db = db;
    }

    // ---- reads ------------------------------------------------------------------

    public async Task<List<ChartPopItem>> LoadItemsAsync(
        int linkshellId, string board, CancellationToken cancellationToken) =>
        await _db.ChartPopItems
            .AsNoTracking()
            .Include(item => item.Credits)
            .Where(item => item.LinkshellId == linkshellId && item.Board == board)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Every chart item the linkshell is HOLDING (at least one copy), across all boards, for
    /// Treasury → Items. In board order, then card order, the way the Charts pages lay them out,
    /// then by item and holder. A row whose board or card the catalog no longer has sorts last.
    /// </summary>
    public async Task<List<ActivityChartHoldingDto>> LoadHoldingsAsync(
        int linkshellId, CancellationToken cancellationToken)
    {
        var rows = await _db.ChartPopItems
            .AsNoTracking()
            .Where(item => item.LinkshellId == linkshellId && item.Quantity > 0)
            .ToListAsync(cancellationToken);

        return rows
            .Select(row =>
            {
                var board = ChartBoardCatalog.Find(row.Board);
                var boardOrder = board is null ? -1 : IndexOfBoard(board);
                var bossOrder = -1;
                if (board is not null)
                {
                    for (var i = 0; i < board.Bosses.Count; i++)
                    {
                        if (string.Equals(board.Bosses[i].Name, row.Boss, StringComparison.OrdinalIgnoreCase))
                        {
                            bossOrder = i;
                            break;
                        }
                    }
                }
                return (Row: row, Board: board,
                    BoardOrder: boardOrder < 0 ? int.MaxValue : boardOrder,
                    BossOrder: bossOrder < 0 ? int.MaxValue : bossOrder);
            })
            .OrderBy(entry => entry.BoardOrder)
            .ThenBy(entry => entry.BossOrder)
            .ThenBy(entry => entry.Row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Row.HeldByCharacterName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new ActivityChartHoldingDto(
                entry.Row.Id,
                entry.Row.Board,
                entry.Board?.Label ?? entry.Row.Board,
                entry.Row.Boss,
                entry.Row.Kind,
                entry.Row.ItemName,
                entry.Row.Quantity,
                string.IsNullOrWhiteSpace(entry.Row.HeldByCharacterName) ? null : entry.Row.HeldByCharacterName.Trim(),
                entry.Row.Notes,
                entry.Row.UpdatedAt))
            .ToList();

        static int IndexOfBoard(ChartBoard board)
        {
            for (var i = 0; i < ChartBoardCatalog.Boards.Count; i++)
            {
                if (ReferenceEquals(ChartBoardCatalog.Boards[i], board))
                {
                    return i;
                }
            }
            return -1;
        }
    }

    public async Task<List<ChartRosterEntry>> LoadRosterAsync(int linkshellId, CancellationToken cancellationToken)
    {
        // Projected flat and folded up after, rather than building the list inside the query: EF
        // cannot translate composing a collection per row, and the alternative is a second round
        // trip per member.
        var rows = await _db.AppUserLinkshells
            .AsNoTracking()
            .Where(member => member.LinkshellId == linkshellId
                && member.CharacterName != null
                && member.CharacterName != "")
            .OrderBy(member => member.CharacterName)
            .Select(member => new
            {
                member.Id,
                member.AppUserId,
                member.CharacterName,
                member.Rank,
                // Left join through the navigation: an unsynced member has no account, so no alts.
                Alt1 = member.AppUser != null ? member.AppUser.AltCharacterName1 : null,
                Alt2 = member.AppUser != null ? member.AppUser.AltCharacterName2 : null,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new ChartRosterEntry(
                row.Id,
                row.AppUserId,
                row.CharacterName!,
                row.Rank,
                new[] { row.Alt1, row.Alt2 }
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!.Trim())
                    // An alt that has been typed into the main slot as well would otherwise be
                    // offered twice in the same list.
                    .Where(name => !string.Equals(name, row.CharacterName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();
    }

    /// <summary>
    /// When the board last changed, for the "Last Updated" stamp. Null when nothing is tracked yet.
    /// Credits count as well as items, because crediting somebody is a change to the board even
    /// though it touches no item row.
    ///
    /// Progress notes used to count as a third source. Nothing writes one any more — the affordance
    /// was redundant with the card's own subtitle — so the query went with the feature rather than
    /// stay as a round trip per page load against a table only history can change.
    /// </summary>
    public async Task<DateTime?> GetLastUpdatedAsync(
        int linkshellId, string board, CancellationToken cancellationToken)
    {
        var itemStamp = await _db.ChartPopItems
            .Where(item => item.LinkshellId == linkshellId && item.Board == board)
            .MaxAsync(item => (DateTime?)item.UpdatedAt, cancellationToken);

        var creditStamp = await _db.ChartPopItemCredits
            .Where(credit => _db.ChartPopItems.Any(item =>
                item.Id == credit.ChartPopItemId && item.LinkshellId == linkshellId && item.Board == board))
            .MaxAsync(credit => (DateTime?)credit.CreditedAt, cancellationToken);

        return new[] { itemStamp, creditStamp }
            .Where(stamp => stamp.HasValue)
            .Select(stamp => stamp!.Value)
            .DefaultIfEmpty()
            .Max() is var newest && newest == default ? null : newest;
    }

    // ---- writes -----------------------------------------------------------------

    /// <summary>
    /// Cleans up what an officer typed. Returns null when the board or boss is unknown, or the item
    /// has no name — the caller refuses rather than storing a row that would group under no card and
    /// be invisible.
    /// </summary>
    /// <param name="kind">ChartItemKinds.Pop or Drop; anything else, including null, reads as Pop.
    /// LAST and defaulted so every caller written before drop items existed compiles unchanged.</param>
    public static ChartPopItemDraft? NormalizeDraft(
        string? board, string? boss, string? itemName, string? heldByCharacterName,
        int quantity, string? notes, string? kind = null)
    {
        // Deliberately NO check of board.AllowsPopItems / AllowsDropItems here.
        //
        // This method is on the EDIT path as well as the add path. Dynamis and Limbus no longer
        // offer an add form, but they still hold rows that officers entered before that, and those
        // rows have to stay editable and deletable. A feature check in here would refuse every one
        // of them and strand the lot. The check belongs in the Add action only - see
        // ActivityDataController.AddChartPopItemAsync - and
        // ChartItemKindTests.NormalizeDraft_DoesNotRefuseARowOnABoardThatNoLongerTakesAdds pins it.
        var canonicalBoard = ChartBoardCatalog.NormalizeBoard(board);
        if (canonicalBoard is null)
        {
            return null;
        }

        var canonicalBoss = ChartBoardCatalog.NormalizeBoss(canonicalBoard, boss);
        if (canonicalBoss is null)
        {
            return null;
        }

        // Canonicalised against the boss's own pop item list, so a name picked out of the dropdown —
        // or typed in whatever case on a board that has no list — is stored one way. Unknown names
        // pass through: see ChartBoardCatalog.NormalizePopItemName for why this does not reject.
        var canonicalKind = ChartBoardCatalog.NormalizeItemKind(kind);
        var name = ChartBoardCatalog.NormalizeItemName(canonicalBoard, canonicalBoss, itemName, canonicalKind);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new ChartPopItemDraft(
            canonicalBoard,
            canonicalBoss,
            Truncate(name, 128)!,
            Truncate(NullIfBlank(heldByCharacterName), 256),
            // Clamped rather than rejected: 0 is a real state ("we used it, none left") and the check
            // constraint refuses negatives anyway.
            Math.Max(0, quantity),
            Truncate(NullIfBlank(notes), 512))
        {
            Kind = canonicalKind,
        };
    }

    /// <summary>
    /// Turns requested credits into trusted ones.
    ///
    /// This is the security boundary. Membership ids are checked against THIS linkshell, and a count
    /// mismatch refuses the whole request rather than quietly recording the rest — without that, a
    /// request could attribute farming credit to a member of somebody else's linkshell. Modeled on
    /// ResolveTreasuryRecipientsAsync, which guards a split the same way.
    ///
    /// Credits naming nobody on the roster are still allowed: an unsynced character is a real member
    /// who really farmed, they just have no account behind the name.
    /// </summary>
    public async Task<(string? Error, List<ChartResolvedCredit> Credits)> ResolveCreditsAsync(
        int linkshellId,
        IReadOnlyList<ChartCreditDraft> drafts,
        CancellationToken cancellationToken)
    {
        var resolved = new List<ChartResolvedCredit>();
        if (drafts.Count == 0)
        {
            return (null, resolved);
        }

        var wantedIds = drafts
            .Where(draft => draft.MembershipId.HasValue)
            .Select(draft => draft.MembershipId!.Value)
            .Distinct()
            .ToList();

        var namesById = new Dictionary<int, string>();
        if (wantedIds.Count > 0)
        {
            namesById = await _db.AppUserLinkshells
                .AsNoTracking()
                .Where(member => member.LinkshellId == linkshellId
                    && wantedIds.Contains(member.Id)
                    && member.CharacterName != null
                    && member.CharacterName != "")
                .ToDictionaryAsync(member => member.Id, member => member.CharacterName!, cancellationToken);

            if (namesById.Count != wantedIds.Count)
            {
                return ("One of those members is not in this linkshell.", resolved);
            }
        }

        // Collapse duplicates by name. Credits are written set-wise, so the same person named twice
        // is a UI slip, not two facts.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var draft in drafts)
        {
            var name = draft.MembershipId.HasValue
                ? namesById[draft.MembershipId.Value]
                : NullIfBlank(draft.CharacterName);

            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            {
                continue;
            }

            resolved.Add(new ChartResolvedCredit(Truncate(name, 256)!));
        }

        if (resolved.Count == 0)
        {
            return ("Name at least one farmer, or clear the credits instead.", resolved);
        }

        return (null, resolved);
    }

    /// <summary>
    /// Hangs a resolved credit list off an item that has NOT been inserted yet, so a new pop item and
    /// the farmers credited with it land in one SaveChanges — an officer naming the farmers while
    /// adding the row cannot end up with the row saved and the credit lost.
    ///
    /// Separate from <see cref="ReplaceCreditsAsync"/> rather than a flag on it: that one keys the
    /// child rows on item.Id, which is still 0 before the insert, and queries the table for what to
    /// delete. Neither is meaningful for a row that does not exist. Does not save.
    /// </summary>
    public static void AttachCredits(
        ChartPopItem item, IReadOnlyList<ChartResolvedCredit> credits, ChartBoardActor actor)
    {
        var now = DateTime.UtcNow;
        foreach (var credit in credits)
        {
            // Added through the navigation property, not the DbSet: EF fills in ChartPopItemId when
            // it inserts the parent.
            item.Credits.Add(new ChartPopItemCredit
            {
                CharacterName = credit.CharacterName,
                CreditedByAppUserId = actor.AppUserId,
                CreditedByCharacterName = actor.CharacterName,
                CreditedAt = now,
            });
        }
    }

    /// <summary>
    /// Replaces the whole credit list for one item. Delete-all then insert, so a duplicate cannot
    /// survive a write and the caller never has to diff two lists. Does not save.
    /// </summary>
    public async Task ReplaceCreditsAsync(
        ChartPopItem item,
        IReadOnlyList<ChartResolvedCredit> credits,
        ChartBoardActor actor,
        CancellationToken cancellationToken)
    {
        var existing = await _db.ChartPopItemCredits
            .Where(credit => credit.ChartPopItemId == item.Id)
            .ToListAsync(cancellationToken);
        _db.ChartPopItemCredits.RemoveRange(existing);

        var now = DateTime.UtcNow;
        foreach (var credit in credits)
        {
            _db.ChartPopItemCredits.Add(new ChartPopItemCredit
            {
                ChartPopItemId = item.Id,
                CharacterName = credit.CharacterName,
                CreditedByAppUserId = actor.AppUserId,
                CreditedByCharacterName = actor.CharacterName,
                CreditedAt = now,
            });
        }

        item.UpdatedAt = now;
    }

    /// <summary>Next sort position on a boss's card, so a new row lands at the bottom.</summary>
    public async Task<int> NextSortOrderAsync(
        int linkshellId, string board, string boss, CancellationToken cancellationToken)
    {
        var highest = await _db.ChartPopItems
            .Where(item => item.LinkshellId == linkshellId && item.Board == board && item.Boss == boss)
            .MaxAsync(item => (int?)item.SortOrder, cancellationToken);
        return (highest ?? -1) + 1;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
