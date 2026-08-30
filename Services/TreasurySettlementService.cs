using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// One member an officer ticked on the "who we owe" list, and the figure the screen was showing
// beside their name at the moment it was ticked.
//
// ExpectedAmount is NOT what gets paid — the server always re-derives that from the lines. It is
// here so a figure that moved between the page rendering and the tick being submitted stops the
// payment rather than quietly handing over a different number than the officer agreed to. Two
// officers settling up at the same time is the case that makes this matter: without the check, one
// of them ticks "Ashira · 300,000", the other records another 200,000 owed to her in the gap, and
// the first officer's tick pays out 500,000 for gil they never handed over.
public sealed record TreasurySettlementPick(string CharacterName, long ExpectedAmount);

// Whose mule a settlement run pays out of, or is paid into. A record rather than two loose strings
// so the pair cannot be passed half-populated.
public sealed record TreasuryHolder(string? AppUserId, string? CharacterName);

// One tick that became a payment.
public sealed record TreasurySettlementLine(string CharacterName, long Amount, string EntryNumber);

// One tick that did not, and the reason to show the officer. Skipping rather than failing the whole
// batch is deliberate: the other ticks were still correct, and refusing all of them because one
// member's figure moved would make the list unusable whenever two officers are working at once.
public sealed record TreasurySettlementSkip(string CharacterName, string Reason);

// Which way the gil moved. The two halves of the balance sheet are both tick-and-record lists now,
// and everything that differs between them — the kind recorded, the words reported, whether gil on
// hand goes up or down — follows from this one value.
public enum TreasurySettlementDirection
{
    // We owed them. Ticking pays it: gil on hand goes DOWN.
    WePaidThem,

    // They owed us. Ticking records the arrival: gil on hand goes UP.
    TheyPaidUs,
}

public sealed record TreasurySettlementResult(
    IReadOnlyList<TreasurySettlementLine> Settled,
    IReadOnlyList<TreasurySettlementSkip> Skipped,
    TreasurySettlementDirection Direction = TreasurySettlementDirection.WePaidThem)
{
    public long TotalPaid => Settled.Sum(line => line.Amount);

    public bool DidNothing => Settled.Count == 0;

    // The sentence both front-ends show. Built here rather than in either of them so the web and the
    // Activity report the same outcome in the same words — the rule TreasuryLabels exists for.
    public string Message
    {
        get
        {
            var paidUs = Direction == TreasurySettlementDirection.TheyPaidUs;
            var parts = new List<string>();
            if (Settled.Count > 0)
            {
                // "Recorded ... from" rather than "Paid ... to": the gil came IN, and reporting an
                // arrival in the words of a payout is how a treasury ends up read backwards.
                parts.Add(paidUs
                    ? $"Recorded {TotalPaid:N0} gil from {Settled.Count} "
                        + (Settled.Count == 1 ? "payer" : "payers") + "."
                    : $"Paid {TotalPaid:N0} gil to {Settled.Count} "
                        + (Settled.Count == 1 ? "member" : "members") + ".");
            }
            foreach (var skip in Skipped)
            {
                parts.Add(paidUs
                    ? $"{skip.CharacterName} was not recorded — {skip.Reason}."
                    : $"{skip.CharacterName} was not paid — {skip.Reason}.");
            }
            return parts.Count > 0 ? string.Join(" ", parts) : "Nobody was ticked, so nothing was recorded.";
        }
    }
}

// Paying members what they are owed straight off the balance sheet, rather than by typing each one
// into the Record form.
//
// The "who we owe" list is derived — TreasuryBalanceService.ProjectByMember sums every what-we-owe
// line by member name, and someone leaves the list when their total reaches zero. So settling from
// it is not a state change on a row somewhere; it is recording one ordinary "We paid a member what
// we owed" transaction per ticked member, which is exactly what an officer would have typed by hand.
// Everything downstream — the balance sheet, the transactions list, Fix and Reverse — therefore
// behaves identically whether a payment came from here or from the form.
//
// Following TreasuryJournalWriter's contract, this never calls SaveChanges: the caller does, so the
// whole batch lands in one transaction or not at all. Half a payout run is the one outcome nobody
// could reconstruct afterwards.
public sealed class TreasurySettlementService
{
    private readonly TreasuryBalanceService _balances;
    private readonly TreasuryJournalWriter _journal;

    public TreasurySettlementService(TreasuryBalanceService balances, TreasuryJournalWriter journal)
    {
        _balances = balances;
        _journal = journal;
    }

    // Paying members off the "who we owe" list.
    public Task<TreasurySettlementResult> SettleAsync(
        int linkshellId,
        IReadOnlyList<TreasurySettlementPick> picks,
        TreasuryActor actor,
        CancellationToken cancellationToken) =>
        SettleAsync(linkshellId, picks, TreasurySettlementDirection.WePaidThem, null, actor, cancellationToken);

    // The same panel on the other side of the sheet: ticking whoever has now paid the LINKSHELL.
    //
    // One method for both, because the two are the same operation with the direction flipped — the
    // list is derived either way, the tick means "in full" either way, and the ExpectedAmount check
    // that stops a stale figure being handed over matters identically in both. Writing the second
    // one separately is how the two would drift.
    public async Task<TreasurySettlementResult> SettleAsync(
        int linkshellId,
        IReadOnlyList<TreasurySettlementPick> picks,
        TreasurySettlementDirection direction,
        // Whose mule the gil leaves from, or lands on. One for the whole run: a payout is one person
        // sitting at one mule handing gil out, so asking per tick would ask the same question eight
        // times. Nullable so the older two-argument overload above still compiles for its callers.
        TreasuryHolder? holder,
        TreasuryActor actor,
        CancellationToken cancellationToken)
    {
        var settled = new List<TreasurySettlementLine>();
        var skipped = new List<TreasurySettlementSkip>();
        if (picks.Count == 0)
        {
            return new TreasurySettlementResult(settled, skipped, direction);
        }

        var paidUs = direction == TreasurySettlementDirection.TheyPaidUs;

        // The same read the list on screen came from, so "in full" means what the books say right
        // now rather than what the client sent.
        var sheet = await _balances.GetBalanceSheetAsync(linkshellId, null, null, cancellationToken);
        var outstanding = (paidUs ? sheet.OwedToUsBy : sheet.OwedToMembers)
            .Where(obligation => obligation.CharacterName is not null)
            .ToDictionary(
                obligation => obligation.CharacterName!,
                obligation => obligation.Amount,
                StringComparer.OrdinalIgnoreCase);

        // One timestamp for the batch: these payments were all handed over in the same sitting, and
        // dating them apart would scatter one payout run across a date boundary.
        var paidAt = DateTime.UtcNow;
        var alreadyPicked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pick in picks)
        {
            var name = pick.CharacterName?.Trim();
            // Same member twice in one submission pays them once. Names are the key ProjectByMember
            // groups on, so a duplicate is the same obligation, not two of them.
            if (string.IsNullOrWhiteSpace(name) || !alreadyPicked.Add(name))
            {
                continue;
            }

            // Not on the list at all, already settled by someone else, or overpaid into a negative.
            // "In full" has no meaning for any of those.
            if (!outstanding.TryGetValue(name, out var due) || due <= 0)
            {
                skipped.Add(new TreasurySettlementSkip(
                    name, paidUs ? "they do not owe anything any more" : "they are not owed anything any more"));
                continue;
            }

            if (due != pick.ExpectedAmount)
            {
                skipped.Add(new TreasurySettlementSkip(
                    name,
                    paidUs
                        ? $"what they owe changed to {due:N0} gil while the page was open"
                        : $"what they are owed changed to {due:N0} gil while the page was open"));
                continue;
            }

            // The ordinary settle transaction, identical to what the Record form used to build.
            // Paying a member adds to what-we-owe (drawing the obligation down) and takes the gil out
            // of gil on hand; being paid draws owed-to-us down and puts the gil in. Both are single
            // catalog kinds with their two categories fixed, so neither surface chooses accounts.
            //
            // Counterparty id is left null to match what the form records for a single name — the
            // projections key on the NAME, and populating one surface's id but not the other's is
            // precisely the split-in-two bug ProjectByCounterparty's comment warns about.
            var entry = await _journal.RecordAsync(
                linkshellId,
                new TreasuryEntryRequest(
                    paidUs
                        ? TreasuryTransactionKinds.TheyPaidWhatTheyOwed
                        : TreasuryTransactionKinds.WePaidWhatWeOwed,
                    due,
                    paidAt,
                    CounterpartyCharacterName: name,
                    // The same mule for every tick in the run, which is what makes the who's-holding-it
                    // list add up: one person handed all of this over, or took all of it in.
                    HolderAppUserId: holder?.AppUserId,
                    HolderCharacterName: holder?.CharacterName),
                actor,
                cancellationToken);

            settled.Add(new TreasurySettlementLine(name, due, entry.EntryNumber));
        }

        return new TreasurySettlementResult(settled, skipped, direction);
    }
}
