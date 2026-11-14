using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One half of one treasury entry, reduced to what a balance needs.
//
// The counterparty is defaulted so every existing construction still compiles: it is only read by
// ProjectByMember, which needs to know whose obligation a what-we-owe line is.
public readonly record struct LedgerLineRow(
    int AccountNumber, long Amount, string? CounterpartyCharacterName = null,
    // Whose mule the gil is on. Only ever set on a gil-on-hand line, and only read by
    // ProjectByHolder — defaulted for the same reason the counterparty above is.
    string? HolderCharacterName = null,
    // The holder's account, when the name was matched to one (see JournalEntryLine.HolderAppUserId).
    // What ProjectByHolder groups on, so a rename does not split one person into two rows.
    string? HolderAppUserId = null);

// One category's running total, as a user should read it.
public sealed record CategoryTotal(int AccountNumber, string AccountName, string AccountClass, long Amount, int EntryCount);

// What one member is still owed. Null name = the obligation was recorded without saying who, which
// was possible before a member became required; the WORD for that lives in TreasuryLabels.
public sealed record MemberObligation(string? CharacterName, long Amount);

// How much of the linkshell's gil one person is carrying. Null name = gil recorded before anyone
// was asked whose mule it went on, or a gil-auction payout, which has no answer to give.
public sealed record GilHolding(string? CharacterName, long Amount);

// The whole sheet from one read: the figures, and the two lists of names behind the two figures that
// have names behind them.
//
// A record rather than a tuple because it now carries three things and a positional `var (_, owed)`
// silently takes the wrong one the moment a fourth arrives. Both lists come from the SAME rows as
// the snapshot, so each always adds up to the figure it sits under — see GetBalanceSheetAsync.
public sealed record TreasuryBalanceSheet(
    TreasurySnapshot Snapshot,
    // Who the linkshell still owes. Ticking one records a payment out.
    IReadOnlyList<MemberObligation> OwedToMembers,
    // Who still owes the linkshell. Ticking one records the gil arriving.
    IReadOnlyList<MemberObligation> OwedToUsBy,
    // And the third figure's names: whose mules the gil on hand is actually sitting on. The last of
    // the three to get a list behind it, and the one that needed it most — the other two are
    // promises, and this is the gil itself.
    IReadOnlyList<GilHolding> GilHolders);

// Everything the treasury summary shows, all of it derived.
public readonly record struct TreasurySnapshot(
    long CashOnHand,
    long OwedToUs,
    long WeOwe,
    long MoneyIn,
    long MoneyOut,
    long StartingBalance)
{
    public long NetChange => MoneyIn - MoneyOut;

    public long NetWorth => CashOnHand + OwedToUs - WeOwe;

    // What the "Adds up" chip reports.
    //
    // This follows from every entry's halves summing to zero, but it is NOT circular: the two sides
    // are computed from independent per-category sums, so a single unbalanced entry anywhere in the
    // linkshell's history makes it false. It is a real check on real data.
    //
    //     what we hold - what we owe  ==  what we started with + (gil in - gil out)
    public bool Balances => NetWorth == StartingBalance + NetChange;
}

// THE reader of a linkshell's gil.
//
// Before this existed, five call sites each answered "how much gil does this linkshell have" and four
// of them disagreed: the web page summed raw values and ignored outflows entirely, and the Activity
// panel computed gil-in minus gil-out by matching two strings, which silently dropped four of the
// seven values the old column actually held. One of those numbers gates whether a gil auction may be
// created.
//
// Balances are DERIVED from the entries and never stored, so there is no cached total to drift — the
// same doctrine the repo already applies to DKP pool balances in DkpPoolBalanceService.
//
// Two rules make every number here correct, and both are easy to get wrong:
//
//   1. DRAFTS ARE EXCLUDED. A draft is a scratch pad; nothing is on the books until it is confirmed.
//
//   2. Gil in and gil out key on the CATEGORY'S NORMAL SIDE, never on the sign of the amount.
//      Reversing a sale puts a POSITIVE amount on a gil-in category, and that has to REDUCE gil in.
//      Splitting on sign would instead count it as gil out and inflate both totals — the entry would
//      net correctly while both gross figures lied. Nothing else is filtered out: a reversal is an
//      ordinary entry and nets against its original automatically, which is why there is no
//      "exclude reversed entries" clause anywhere. Adding one is how you get a reversed 8M sale to
//      read as -8M.
public sealed class TreasuryBalanceService
{
    private readonly ApplicationDbContext _db;

    public TreasuryBalanceService(ApplicationDbContext db)
    {
        _db = db;
    }

    // The projection as a pure function, shared by the live read path and the tests, so what the
    // tests prove is what production computes. Mirrors DkpPoolBalanceService.Project.
    public static TreasurySnapshot Project(IEnumerable<LedgerLineRow> lines)
    {
        long cash = 0, owedToUs = 0, weOwe = 0, moneyIn = 0, moneyOut = 0, starting = 0;

        foreach (var line in lines)
        {
            var presented = line.Amount * LedgerAccountClasses.NormalSignForNumber(line.AccountNumber);
            switch (LedgerAccountClasses.FromNumber(line.AccountNumber))
            {
                case LedgerAccountClasses.Holds:
                    if (line.AccountNumber == TreasuryAccounts.GilOnHand) { cash += presented; }
                    else { owedToUs += presented; }
                    break;
                case LedgerAccountClasses.Owes:
                    weOwe += presented;
                    break;
                case LedgerAccountClasses.Worth:
                    starting += presented;
                    break;
                case LedgerAccountClasses.MoneyIn:
                    moneyIn += presented;
                    break;
                case LedgerAccountClasses.MoneyOut:
                    moneyOut += presented;
                    break;
            }
        }

        return new TreasurySnapshot(cash, owedToUs, weOwe, moneyIn, moneyOut, starting);
    }

    // Who the linkshell still owes, and how much each. Also pure, and fed the SAME rows as Project,
    // so the breakdown always adds up to the what-we-owe figure it sits under.
    //
    // Members are keyed on their NAME, never on their account id, and that is load-bearing rather
    // than lazy. The two ways an obligation gets recorded populate the account id differently: a
    // split resolves its recipients from the roster and stores their ids, while a single-member
    // entry stores null. Keying on "id if present, else name" would therefore file one payout
    // under the id and its later settlement under the name, and show the same person twice — once
    // owed, once owed a negative — while still summing to the right total. The name is the only
    // field the writer guarantees on every line that can carry a member.
    //
    // Known limit: renaming a character splits them into two rows. Matching against the roster does
    // not help, because a line recorded under the old name matches neither its name nor its id.
    public static List<MemberObligation> ProjectByMember(IEnumerable<LedgerLineRow> lines) =>
        ProjectByCounterparty(lines, TreasuryAccounts.WeOwe);

    // The mirror image: who owes the LINKSHELL, and how much each. Same rules, other direction.
    //
    // The two lists are what make both halves of the balance sheet tickable — one settles by paying
    // a member, the other by recording that someone paid us — so they have to be projected the same
    // way or one side would drift from the figure above it in a way the other never does.
    //
    // The names on these lines are usually NOT members: whoever owes a linkshell gil is typically
    // another linkshell or a buyer, typed in free text. That costs nothing here, because this keys
    // on the name either way and never consults the roster.
    public static List<MemberObligation> ProjectByDebtor(IEnumerable<LedgerLineRow> lines) =>
        ProjectByCounterparty(lines, TreasuryAccounts.OwedToUs);

    // Whose mules the gil on hand is sitting on. Same shape as the two lists above and fed the same
    // rows, so this ALWAYS adds up to the gil-on-hand figure it sits under — that is the whole
    // reason the holder lives on the signed cash line rather than in a table of its own. Gil
    // arriving on a mule is a positive line, gil leaving it is a negative one, and no separate
    // bookkeeping step can be forgotten.
    //
    // Grouped on the holder's ACCOUNT when the line has one, and on the name when it does not, so
    // a member who renames their character stays one row. Every write fills the account from the
    // name (TreasuryJournalWriter), and the FillJournalLineHolderAccounts migration filled it on
    // older lines wherever the name matched exactly one member.
    //
    // A line with a name but no account (a name that matched nobody, or more than one member,
    // when it was written) joins the account row that recorded the SAME name, when exactly one
    // account did. Without that, a sale filed under the account and a later spend filed under the
    // name alone would show one person twice. When two accounts recorded that name, it stays its
    // own row: guessing between two people is worse than one extra row.
    //
    // The row shows the newest name recorded for it. The lines arrive in the order they were
    // written (GetBalanceSheetAsync sorts them), so after a rename the new name wins.
    //
    // Unlike the obligation lists, a null name is KEPT rather than dropped: every line recorded
    // before holders existed has one, as does every gil-auction payout, and hiding that bucket
    // would make the rows visibly fail to add up to the figure above them. TreasuryLabels.
    // UnnamedHolder is what the app calls it.
    public static List<GilHolding> ProjectByHolder(IEnumerable<LedgerLineRow> lines)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var cash = lines.Where(line => line.AccountNumber == TreasuryAccounts.GilOnHand).ToList();

        // Which accounts each name was recorded under.
        var accountsByName = cash
            .Where(line => Clean(line.HolderAppUserId) is not null && Clean(line.HolderCharacterName) is not null)
            .GroupBy(line => Clean(line.HolderCharacterName)!.ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(line => Clean(line.HolderAppUserId)!).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        var totals = new Dictionary<string, (string? Name, long Amount)>(StringComparer.Ordinal);

        foreach (var line in cash)
        {
            var name = Clean(line.HolderCharacterName);
            var account = Clean(line.HolderAppUserId);
            if (account is null
                && name is not null
                && accountsByName.TryGetValue(name.ToLowerInvariant(), out var accounts)
                && accounts.Count == 1)
            {
                account = accounts[0];
            }

            // Prefixed so an account id can never collide with a character name.
            var key = account is not null
                ? "account:" + account
                : "name:" + (name?.ToLowerInvariant() ?? string.Empty);

            // Gil on hand's normal side is positive, so the stored amount already reads the right
            // way round and no presentation flip is needed here.
            totals[key] = totals.TryGetValue(key, out var running)
                ? (name ?? running.Name, running.Amount + line.Amount)
                : (name, line.Amount);
        }

        // A holder who has handed everything over drops off. A NEGATIVE is kept, for the same reason
        // it is on the obligation lists: it means more gil was spent off that mule than was ever
        // recorded onto it, and burying that would both break the add-up and hide the mistake.
        return totals.Values
            .Where(entry => entry.Amount != 0)
            .OrderByDescending(entry => entry.Amount)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new GilHolding(entry.Name, entry.Amount))
            .ToList();
    }

    private static List<MemberObligation> ProjectByCounterparty(
        IEnumerable<LedgerLineRow> lines, int accountNumber)
    {
        var totals = new Dictionary<string, (string? Name, long Amount)>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (line.AccountNumber != accountNumber)
            {
                continue;
            }

            // Presented, so an obligation reads positive and settling it reads negative — the same
            // normal-side rule every other total here follows.
            var presented = line.Amount * LedgerAccountClasses.NormalSignForNumber(line.AccountNumber);
            var name = string.IsNullOrWhiteSpace(line.CounterpartyCharacterName)
                ? null
                : line.CounterpartyCharacterName.Trim();
            var key = name?.ToLowerInvariant() ?? string.Empty;

            totals[key] = totals.TryGetValue(key, out var running)
                ? (running.Name ?? name, running.Amount + presented)
                : (name, presented);
        }

        // Settled members drop off; anyone still owed stays, largest first. A NEGATIVE total is kept
        // deliberately — nothing stops an officer settling more than was recorded, and hiding it
        // would both break the add-up and bury the only evidence of the mistake.
        return totals.Values
            .Where(entry => entry.Amount != 0)
            .OrderByDescending(entry => entry.Amount)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new MemberObligation(entry.Name, entry.Amount))
            .ToList();
    }

    // How much gil this linkshell has. THE canonical balance — the gil solvency check reads this, so a
    // wrong answer lets a linkshell auction gil it does not hold.
    public async Task<long> GetCashOnHandAsync(int linkshellId, CancellationToken cancellationToken)
    {
        return await ConfirmedLines(linkshellId)
            .Where(line => line.AccountNumber == TreasuryAccounts.GilOnHand)
            // Nullable sum: SQL SUM() over no rows is NULL, and an empty treasury is the day-one case.
            .SumAsync(line => (long?)line.Amount, cancellationToken) ?? 0L;
    }

    // Balances for many linkshells in one round trip, for the Activity overview payload. Linkshells
    // with nothing recorded are absent; callers default to 0.
    public async Task<Dictionary<int, long>> GetCashOnHandByLinkshellAsync(
        IReadOnlyCollection<int> linkshellIds, CancellationToken cancellationToken)
    {
        if (linkshellIds.Count == 0)
        {
            return new Dictionary<int, long>();
        }

        return await _db.JournalEntryLines
            .Where(line => linkshellIds.Contains(line.LinkshellId)
                && line.AccountNumber == TreasuryAccounts.GilOnHand
                && line.JournalEntry!.Status == JournalEntryStatuses.Confirmed)
            .GroupBy(line => line.LinkshellId)
            .Select(group => new { LinkshellId = group.Key, Total = group.Sum(line => (long?)line.Amount) ?? 0L })
            .ToDictionaryAsync(item => item.LinkshellId, item => item.Total, cancellationToken);
    }

    // Everything the summary card shows. Holdings are always since-inception (gil on hand does not
    // reset); the flow totals honour the date range.
    public async Task<TreasurySnapshot> GetSnapshotAsync(
        int linkshellId, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken) =>
        (await GetBalanceSheetAsync(linkshellId, fromUtc, toUtc, cancellationToken)).Snapshot;

    // The summary card AND the breakdown of who is still owed, from ONE read of the lines.
    //
    // Fetching the breakdown separately would be two reads with no transaction between them, so a
    // confirm landing in the gap would make the expanded rows genuinely not add up to the figure
    // above them. Projecting both from the same list makes that impossible rather than unlikely.
    //
    // The breakdown ignores the date range on purpose: holdings are since-inception, so a ranged
    // breakdown could not add up to the what-we-owe figure it sits under.
    public async Task<TreasuryBalanceSheet> GetBalanceSheetAsync(
        int linkshellId, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken)
    {
        // In the order they were written, so ProjectByHolder shows each holder's newest name.
        var holdings = await ConfirmedLines(linkshellId)
            .Where(line => line.AccountNumber < 4000)
            .OrderBy(line => line.JournalEntryId)
            .ThenBy(line => line.LineNumber)
            .Select(line => new LedgerLineRow(
                line.AccountNumber, line.Amount, line.CounterpartyCharacterName, line.HolderCharacterName,
                line.HolderAppUserId))
            .ToListAsync(cancellationToken);

        // All five arguments spelled out: an EF expression tree cannot call a constructor that
        // relies on an optional one. Flow lines carry neither a member nor a holder — only
        // what-we-owe is attributable, and only gil on hand sits on a mule.
        var flows = await InRange(ConfirmedLines(linkshellId), fromUtc, toUtc)
            .Where(line => line.AccountNumber >= 4000)
            .Select(line => new LedgerLineRow(line.AccountNumber, line.Amount, null, null, null))
            .ToListAsync(cancellationToken);

        return new TreasuryBalanceSheet(
            Project(holdings.Concat(flows)),
            ProjectByMember(holdings),
            ProjectByDebtor(holdings),
            // Holdings, not the ranged flows: gil on hand is since-inception, so a ranged breakdown
            // could not add up to the figure it sits under — the same rule the two lists above follow.
            ProjectByHolder(holdings));
    }

    // Per-category totals — the "does it add up" breakdown, in the officer's categories.
    public async Task<List<CategoryTotal>> GetCategoryTotalsAsync(
        int linkshellId, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken)
    {
        var rows = await InRange(ConfirmedLines(linkshellId), fromUtc, toUtc)
            .GroupBy(line => new { line.AccountNumber, line.AccountName })
            .Select(group => new
            {
                group.Key.AccountNumber,
                group.Key.AccountName,
                Amount = group.Sum(line => (long?)line.Amount) ?? 0L,
                // Entries, not lines. A split payout puts several lines on one category, so
                // counting rows here would report one payout as eight.
                EntryCount = group.Select(line => line.JournalEntryId).Distinct().Count(),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new CategoryTotal(
                row.AccountNumber,
                row.AccountName,
                LedgerAccountClasses.FromNumber(row.AccountNumber) ?? string.Empty,
                row.Amount * LedgerAccountClasses.NormalSignForNumber(row.AccountNumber),
                row.EntryCount))
            .OrderBy(row => row.AccountNumber)
            .ToList();
    }

    // Drafts are not on the books. Reversals ARE — they are ordinary entries and net against their
    // originals, so nothing about reversal state is filtered here.
    private IQueryable<JournalEntryLine> ConfirmedLines(int linkshellId) =>
        _db.JournalEntryLines
            .Where(line => line.LinkshellId == linkshellId
                && line.JournalEntry!.Status == JournalEntryStatuses.Confirmed);

    private static IQueryable<JournalEntryLine> InRange(
        IQueryable<JournalEntryLine> source, DateTime? fromUtc, DateTime? toUtc)
    {
        if (fromUtc.HasValue)
        {
            source = source.Where(line => line.TransactionDate >= fromUtc.Value);
        }
        if (toUtc.HasValue)
        {
            source = source.Where(line => line.TransactionDate < toUtc.Value);
        }
        return source;
    }
}
