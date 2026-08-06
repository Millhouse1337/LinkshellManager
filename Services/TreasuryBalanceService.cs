using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One half of one treasury entry, reduced to what a balance needs.
//
// The counterparty is defaulted so every existing construction still compiles: it is only read by
// ProjectByMember, which needs to know whose obligation a what-we-owe line is.
public readonly record struct LedgerLineRow(
    int AccountNumber, long Amount, string? CounterpartyCharacterName = null);

// One category's running total, as a user should read it.
public sealed record CategoryTotal(int AccountNumber, string AccountName, string AccountClass, long Amount, int EntryCount);

// What one member is still owed. Null name = the obligation was recorded without saying who, which
// was possible before a member became required; the WORD for that lives in TreasuryLabels.
public sealed record MemberObligation(string? CharacterName, long Amount);

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
    // entry stores null on both surfaces. Keying on "id if present, else name" would therefore file
    // one payout under the id and its later settlement under the name, and show the same person
    // twice — once owed, once owed a negative — while still summing to the right total. The name is
    // the only field the writer guarantees on every line that can carry a member.
    //
    // Known limit: renaming a character splits them into two rows. Matching against the roster does
    // not help, because a line recorded under the old name matches neither its name nor its id.
    public static List<MemberObligation> ProjectByMember(IEnumerable<LedgerLineRow> lines)
    {
        var totals = new Dictionary<string, (string? Name, long Amount)>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (line.AccountNumber != TreasuryAccounts.WeOwe)
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
    public async Task<(TreasurySnapshot Snapshot, List<MemberObligation> OwedToMembers)> GetBalanceSheetAsync(
        int linkshellId, DateTime? fromUtc, DateTime? toUtc, CancellationToken cancellationToken)
    {
        var holdings = await ConfirmedLines(linkshellId)
            .Where(line => line.AccountNumber < 4000)
            .Select(line => new LedgerLineRow(
                line.AccountNumber, line.Amount, line.CounterpartyCharacterName))
            .ToListAsync(cancellationToken);

        // All three arguments spelled out: an EF expression tree cannot call a constructor that
        // relies on an optional one. Flow lines carry no member — only what-we-owe is attributable.
        var flows = await InRange(ConfirmedLines(linkshellId), fromUtc, toUtc)
            .Where(line => line.AccountNumber >= 4000)
            .Select(line => new LedgerLineRow(line.AccountNumber, line.Amount, null))
            .ToListAsync(cancellationToken);

        return (Project(holdings.Concat(flows)), ProjectByMember(holdings));
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

    // How many entries landed in a catch-all category. Surfaced as a count chip so the leftovers from
    // the one-time conversion are a finite, visible chore rather than permanent silent noise.
    public async Task<int> GetUncategorizedCountAsync(int linkshellId, CancellationToken cancellationToken)
    {
        return await ConfirmedLines(linkshellId)
            .Where(line => line.AccountNumber == TreasuryAccounts.OtherMoneyIn
                || line.AccountNumber == TreasuryAccounts.OtherMoneyOut)
            .Select(line => line.JournalEntryId)
            .Distinct()
            .CountAsync(cancellationToken);
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
