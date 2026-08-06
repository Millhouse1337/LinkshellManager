using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

/// <summary>The four states a ledger cell can be in. Strings on the wire, like JournalEntryStatuses.</summary>
public static class ChartCreditStatuses
{
    public const string Credited = "Credited";
    public const string Partial = "Partial";
    public const string None = "None";

    /// <summary>
    /// The boss has no pop items entered at all, so "credited" is a question that cannot be asked
    /// yet. Deliberately NOT folded into Credited: a boss with nothing tracked is vacuously fully
    /// credited, and showing that as a green tick tells a linkshell everyone is square on Absolute
    /// Virtue when in fact nobody has typed its sins in.
    /// </summary>
    public const string NotTracked = "NotTracked";
}

/// <summary>One roster entry, reduced to what the ledger needs.</summary>
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

/// <summary>One cell of the member × boss grid. The UI renders "{CreditedItems} / {TotalItems}".</summary>
public sealed record ChartLedgerCell(string Boss, string Status, string Detail, int CreditedItems, int TotalItems);

/// <summary>
/// One member's row. <paramref name="IsCurrentMember"/> is false for a credited ex-member.
///
/// <paramref name="Rank"/> is the linkshell rank shown under the name. The mockup put a JOB there,
/// but nothing in the roster records a main job — AppUser.JobLevels is a per-job level array, and
/// picking a "main" out of it would be a guess presented as fact. Rank is real data in the same slot.
/// </summary>
public sealed record ChartLedgerRow(
    int? MembershipId,
    string CharacterName,
    bool IsCurrentMember,
    string? Rank,
    IReadOnlyList<ChartLedgerCell> Cells,
    int TotalCredited,
    int TotalTracked)
{
    /// <summary>The ledger's trailing percentage. 0 when nothing is tracked — never a divide by zero.</summary>
    public int CreditedPercent => TotalTracked == 0 ? 0 : (int)Math.Round(100d * TotalCredited / TotalTracked);
}

/// <summary>The whole grid: column order plus rows.</summary>
public sealed record ChartLedger(IReadOnlyList<string> Bosses, IReadOnlyList<ChartLedgerRow> Rows);

/// <summary>What an officer typed, before it is trusted.</summary>
public sealed record ChartPopItemDraft(
    string Board, string Boss, string ItemName, string? HeldByCharacterName, int? HeldByMembershipId,
    int Quantity, string? Notes);

/// <summary>One credit as requested. Either a roster membership id, or just a name for an unsynced farmer.</summary>
public sealed record ChartCreditDraft(int? MembershipId, string? CharacterName, string? Detail);

/// <summary>A resolved, trusted credit — the membership id has been checked against the linkshell.</summary>
public sealed record ChartResolvedCredit(int? MembershipId, string CharacterName, string? Detail);

/// <summary>Who is performing a write, for the audit columns.</summary>
public readonly record struct ChartBoardActor(string? AppUserId, string? CharacterName);

/// <summary>
/// THE reader and writer of the Charts boards.
///
/// The rules live here rather than in the controllers for two reasons. One, both surfaces use them —
/// the Activity API and the website's ChartsController are both thin callers, so the two cannot
/// derive a different ledger from the same rows (the same reason ItemSaleRecorder is shared). Two,
/// the tests in this repo are service-level, so anything worth pinning has to be reachable without
/// spinning up a controller.
///
/// Board-agnostic throughout: Sky's five gods and Sea's eight Jailers differ only by what
/// ChartBoardCatalog says, never by a branch in here.
///
/// The central design decision: farming credit is stored ONLY per (pop item × person). The ledger's
/// per-boss grid is derived from those rows on every read and never stored. The boss card shows a
/// per-item farmer count and the ledger shows a per-boss fraction; only the finer grain can be the
/// stored one, and deriving the coarser view is what makes it impossible for the two halves of the
/// same page to disagree.
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

    // ---- the derivation (pure — this is the testable core) ----------------------

    /// <summary>
    /// Builds the Farming Credit Ledger from a board's pop items and the roster. No database, no
    /// clock, no hidden state: the same inputs always give the same grid.
    ///
    /// For each (person, boss) where the boss has N item rows and the person is credited on C:
    ///
    ///   N == 0        NotTracked  "No items tracked" — nothing entered, so nothing to be square on
    ///   C == 0        None        "0 / N"
    ///   0 &lt; C &lt; N   Partial     "2 / 3"
    ///   C == N        Credited    "3 / 3"
    ///
    /// The row total is items credited over items tracked across the whole board, plus a percentage.
    /// Counting ITEMS rather than fully-covered bosses is what lets one number compare a five-god
    /// board with an eight-Jailer one, and it does not let somebody who cleared one small boss
    /// outrank somebody who did most of the work on a large one.
    ///
    /// Bosses with nothing tracked contribute 0 to BOTH sides of that fraction, so an empty board
    /// reads 0 / 0 rather than everyone sitting at 100%.
    /// </summary>
    public static ChartLedger BuildLedger(
        ChartBoard board,
        IReadOnlyList<ChartPopItem> items,
        IReadOnlyList<ChartRosterEntry> roster)
    {
        var bosses = board.Bosses.Select(boss => boss.Name).ToList();

        var itemsByBoss = bosses.ToDictionary(
            boss => boss,
            boss => items.Where(item => string.Equals(item.Boss, boss, StringComparison.OrdinalIgnoreCase)).ToList(),
            StringComparer.OrdinalIgnoreCase);

        // Everyone who appears anywhere: the roster, plus any credited name that has since left.
        // Removing somebody from the linkshell must not erase the fact that they farmed.
        var people = new List<ChartLedgerRow>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in roster)
        {
            if (!seenNames.Add(member.CharacterName))
            {
                continue;
            }
            people.Add(BuildRow(
                member.MembershipId, member.CharacterName, isCurrentMember: true, member.Rank, bosses, itemsByBoss));
        }

        var departed = items
            .SelectMany(item => item.Credits)
            .Where(credit => !string.IsNullOrWhiteSpace(credit.CharacterName))
            .Select(credit => credit.CharacterName.Trim())
            .Where(name => !seenNames.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var name in departed)
        {
            people.Add(BuildRow(
                membershipId: null, name, isCurrentMember: false, rank: null, bosses, itemsByBoss));
        }

        return new ChartLedger(bosses, people);
    }

    private static ChartLedgerRow BuildRow(
        int? membershipId,
        string characterName,
        bool isCurrentMember,
        string? rank,
        IReadOnlyList<string> bosses,
        Dictionary<string, List<ChartPopItem>> itemsByBoss)
    {
        var cells = new List<ChartLedgerCell>(bosses.Count);
        var totalCredited = 0;
        var totalTracked = 0;

        foreach (var boss in bosses)
        {
            var bossItems = itemsByBoss[boss];
            var total = bossItems.Count;
            // Matching is by NAME, case-insensitively, not by membership id: an unsynced farmer has
            // no id, and a member who re-synced would otherwise lose their history.
            var credited = bossItems.Count(item => item.Credits.Any(
                credit => string.Equals(credit.CharacterName?.Trim(), characterName, StringComparison.OrdinalIgnoreCase)));

            totalCredited += credited;
            totalTracked += total;
            cells.Add(new ChartLedgerCell(boss, StatusFor(credited, total), DetailFor(credited, total), credited, total));
        }

        return new ChartLedgerRow(
            membershipId, characterName, isCurrentMember, rank, cells, totalCredited, totalTracked);
    }

    private static string StatusFor(int credited, int total)
    {
        if (total == 0) return ChartCreditStatuses.NotTracked;
        if (credited == 0) return ChartCreditStatuses.None;
        return credited >= total ? ChartCreditStatuses.Credited : ChartCreditStatuses.Partial;
    }

    // The words are decided here, once, so both surfaces say them the same way. An em dash rather
    // than "0 / 0" for an untracked boss: a fraction implies there was something to farm.
    private static string DetailFor(int credited, int total) =>
        total == 0 ? "—" : $"{credited} / {total}";

    // ---- writes -----------------------------------------------------------------

    /// <summary>
    /// Cleans up what an officer typed. Returns null when the board or boss is unknown, or the item
    /// has no name — the caller refuses rather than storing a row that would group under no card and
    /// be invisible.
    /// </summary>
    public static ChartPopItemDraft? NormalizeDraft(
        string? board, string? boss, string? itemName, string? heldByCharacterName,
        int? heldByMembershipId, int quantity, string? notes)
    {
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
        var name = ChartBoardCatalog.NormalizePopItemName(canonicalBoard, canonicalBoss, itemName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new ChartPopItemDraft(
            canonicalBoard,
            canonicalBoss,
            Truncate(name, 128)!,
            Truncate(NullIfBlank(heldByCharacterName), 256),
            heldByMembershipId,
            // Clamped rather than rejected: 0 is a real state ("we used it, none left") and the check
            // constraint refuses negatives anyway.
            Math.Max(0, quantity),
            Truncate(NullIfBlank(notes), 512));
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

            resolved.Add(new ChartResolvedCredit(
                draft.MembershipId, Truncate(name, 256)!, Truncate(NullIfBlank(draft.Detail), 256)));
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
                LinkshellId = item.LinkshellId,
                MembershipId = credit.MembershipId,
                CharacterName = credit.CharacterName,
                Detail = credit.Detail,
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
                LinkshellId = item.LinkshellId,
                MembershipId = credit.MembershipId,
                CharacterName = credit.CharacterName,
                Detail = credit.Detail,
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
