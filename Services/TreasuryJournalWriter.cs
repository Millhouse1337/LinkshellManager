using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Who is recording this.
public sealed record TreasuryActor(string? AppUserId, string? CharacterName);

// One member getting a share of a split.
//
// AppUserId is nullable because an unsynced character is a real member of a linkshell and can be
// owed real gil; they just have no account behind them. The name is always recorded, so who was
// paid stays readable either way.
public sealed record TreasuryRecipient(string? AppUserId, string CharacterName);

// What happened, in the terms an officer picked it.
//
// There is deliberately no way to pass a pair of categories: the pair comes from
// TreasuryTransactionKinds and nothing else. That is the property that makes every entry correct by
// construction rather than correct if the caller was careful.
public sealed record TreasuryEntryRequest(
    string TransactionKind,
    long Amount,
    DateTime TransactionDateUtc,
    string? Memo = null,
    string? CounterpartyAppUserId = null,
    string? CounterpartyCharacterName = null,
    // Whose mule the gil lands on or leaves. Recorded against the gil-on-hand half ONLY, so summing
    // by holder always adds back up to gil on hand — see JournalEntryLine.HolderCharacterName.
    //
    // Optional here rather than required, because two callers genuinely have no answer: a gil
    // auction close pays a winner without anyone saying which mule it came off, and a reversal
    // copies whatever the original said. Everything an officer types goes through a form that
    // refuses to submit without one.
    string? HolderAppUserId = null,
    string? HolderCharacterName = null,
    string Source = JournalEntrySources.Manual,
    int? SourceItemId = null,
    int? SourceAuctionItemId = null,
    int? SourceAuctionHistoryId = null,
    // Everyone sharing the amount, for a kind that names a SplitAccount. Null or empty records the
    // ordinary two halves, so every existing caller behaves exactly as it did.
    //
    // Callers must have already checked these are members of this linkshell. The writer trusts the
    // list, the same way it trusts CounterpartyCharacterName.
    IReadOnlyList<TreasuryRecipient>? Recipients = null);

// Thrown when a request names a "what happened" option that does not exist.
public sealed class UnknownTreasuryTransactionKindException : Exception
{
    public UnknownTreasuryTransactionKindException(string? kind)
        : base($"'{kind}' is not something that can happen to the treasury.")
    {
    }
}

// Thrown when something tries to change an entry that is already on the books.
public sealed class ConfirmedTreasuryEntryException : Exception
{
    public ConfirmedTreasuryEntryException(string entryNumber)
        : base($"Entry {TreasuryLabels.EntryReference(entryNumber)} is already recorded. "
            + "Fix it or reverse it instead of editing it.")
    {
    }
}

// THE single place gil moves.
//
// Before this, six call sites each hand-rolled the same few lines: pick an EntryType string, decide a
// sign, and add a RevenueEntry. That is fine right up until a seventh thing has to happen on every
// write — like keeping both halves balanced, or honouring a lock — at which point six copies are six
// chances to drift. Two of them had already drifted into a different vocabulary.
//
// Everything here upholds two invariants, both asserted in DEBUG by ApplicationDbContext:
//   INV-2  an entry's halves always sum to zero, which is what makes gil on hand provable
//   INV-3  a confirmed entry is never updated; it is reversed or corrected
//
// This service never calls SaveChanges. Callers batch their own save, because closing a gil auction
// has to stay a single transaction — the same contract as DkpLedgerWriter.
public sealed class TreasuryJournalWriter
{
    private readonly ApplicationDbContext _db;
    private readonly LedgerAccountProvisioner _accounts;
    private readonly LedgerPeriodGuard _periods;

    // Next Sequence per linkshell, lazily seeded from MAX(Sequence) + 1. Same scheme as
    // DkpLedgerWriter: it is what stops two entries written in one request from colliding on the
    // unique (LinkshellId, Sequence) index.
    private readonly Dictionary<int, int> _nextSequence = new();

    // Categories for the request, so a multi-item auction close resolves them once.
    private readonly Dictionary<int, Dictionary<int, LedgerAccount>> _accountsByLinkshell = new();

    public TreasuryJournalWriter(
        ApplicationDbContext db, LedgerAccountProvisioner accounts, LedgerPeriodGuard periods)
    {
        _db = db;
        _accounts = accounts;
        _periods = periods;
    }

    // Build an editable draft. Nothing is on the books until ConfirmAsync.
    public async Task<JournalEntry> DraftAsync(
        int linkshellId, TreasuryEntryRequest request, TreasuryActor actor, CancellationToken cancellationToken)
    {
        var kind = TreasuryTransactionKinds.Find(request.TransactionKind)
            ?? throw new UnknownTreasuryTransactionKindException(request.TransactionKind);

        var linkshellName = await _db.Linkshells
            .AsNoTracking()
            .Where(linkshell => linkshell.Id == linkshellId)
            .Select(linkshell => linkshell.LinkshellName)
            .FirstOrDefaultAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var sequence = await NextSequenceAsync(linkshellId, cancellationToken);
        var entry = new JournalEntry
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshellName,
            Sequence = sequence,
            EntryNumber = FormatEntryNumber(sequence),
            TransactionDate = request.TransactionDateUtc,
            Status = JournalEntryStatuses.Draft,
            Kind = kind.EntryKind,
            Source = request.Source,
            TransactionKind = kind.Key,
            Memo = Trim(request.Memo, 1024),
            CreatedAt = now,
            CreatedByAppUserId = actor.AppUserId,
            CreatedByCharacterName = actor.CharacterName,
            SourceItemId = request.SourceItemId,
            SourceAuctionItemId = request.SourceAuctionItemId,
            SourceAuctionHistoryId = request.SourceAuctionHistoryId,
        };

        await BuildHalvesAsync(entry, kind, request, cancellationToken);
        _db.JournalEntries.Add(entry);
        return entry;
    }

    // Rebuild a draft from a fresh request. Only ever a draft — a confirmed entry is immutable.
    public async Task UpdateDraftAsync(
        JournalEntry draft, TreasuryEntryRequest request, CancellationToken cancellationToken)
    {
        RequireDraft(draft);
        var kind = TreasuryTransactionKinds.Find(request.TransactionKind)
            ?? throw new UnknownTreasuryTransactionKindException(request.TransactionKind);

        _db.JournalEntryLines.RemoveRange(draft.Lines);
        draft.Lines.Clear();

        draft.TransactionDate = request.TransactionDateUtc;
        draft.Kind = kind.EntryKind;
        draft.TransactionKind = kind.Key;
        draft.Memo = Trim(request.Memo, 1024);
        await BuildHalvesAsync(draft, kind, request, cancellationToken);
    }

    // A draft is the only thing that can be deleted, because nothing was ever recorded.
    public void DiscardDraft(JournalEntry draft)
    {
        RequireDraft(draft);
        _db.JournalEntryLines.RemoveRange(draft.Lines);
        _db.JournalEntries.Remove(draft);
    }

    // Put a draft on the books.
    public async Task<JournalEntry> ConfirmAsync(
        JournalEntry draft, TreasuryActor actor, CancellationToken cancellationToken)
    {
        if (JournalEntryStatuses.IsConfirmed(draft.Status))
        {
            // Idempotent: confirming twice is a double-tap, not an error.
            return draft;
        }
        RequireDraft(draft);
        await _periods.EnsureOpenAsync(draft.LinkshellId, draft.TransactionDate, cancellationToken);

        draft.Status = JournalEntryStatuses.Confirmed;
        draft.ConfirmedAt = DateTime.UtcNow;
        draft.ConfirmedByAppUserId = actor.AppUserId;
        draft.ConfirmedByCharacterName = actor.CharacterName;
        return draft;
    }

    // Draft and confirm in one step. What every machine-recorded entry uses — an item sale and a gil
    // auction close are records of gil that has already moved, so there is nothing to review.
    public async Task<JournalEntry> RecordAsync(
        int linkshellId, TreasuryEntryRequest request, TreasuryActor actor, CancellationToken cancellationToken)
    {
        var entry = await DraftAsync(linkshellId, request, actor, cancellationToken);
        return await ConfirmAsync(entry, actor, cancellationToken);
    }

    // Cancel a confirmed entry by recording its opposite. The original stays on the books, chipped
    // "Reversed", so what was believed and when survives.
    public async Task<JournalEntry> ReverseAsync(
        JournalEntry original, string reason, TreasuryActor actor, CancellationToken cancellationToken)
    {
        return await MirrorAsync(original, reason, actor, JournalEntryKinds.Reversal, cancellationToken);
    }

    // Fix a confirmed entry: one reversal plus one replacement, recorded together, so correcting a
    // fat-fingered amount is a single action for the officer rather than two.
    public async Task<(JournalEntry Reversal, JournalEntry Replacement)> CorrectAsync(
        JournalEntry original,
        TreasuryEntryRequest corrected,
        string reason,
        TreasuryActor actor,
        CancellationToken cancellationToken)
    {
        var reversal = await MirrorAsync(original, reason, actor, JournalEntryKinds.Reversal, cancellationToken);

        var replacement = await DraftAsync(original.LinkshellId, corrected, actor, cancellationToken);
        replacement.Kind = JournalEntryKinds.Correction;
        replacement.CorrectionReason = Trim(reason, 512);
        replacement.ReversesJournalEntryId = original.Id;
        await ConfirmAsync(replacement, actor, cancellationToken);

        return (reversal, replacement);
    }

    // Gil on hand including entries this request has built but not yet saved. Closing a gil auction
    // settles several items in ONE save, so the second item has to see the first item's payout or the
    // solvency check reads a stale balance and lets the treasury go negative. This is the role
    // DkpLedgerWriter.GetPoolBalanceAsync plays for DKP.
    //
    // The unsaved half is read from the ChangeTracker rather than tracked in a field of our own. A
    // running total kept by hand double-counts the moment a caller saves mid-request — it has no way to
    // know its entries have become committed and are now also in the query below.
    public async Task<long> GetCashOnHandAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var committed = await _db.JournalEntryLines
            .Where(line => line.LinkshellId == linkshellId
                && line.AccountNumber == TreasuryAccounts.GilOnHand
                && line.JournalEntry!.Status == JournalEntryStatuses.Confirmed)
            .SumAsync(line => (long?)line.Amount, cancellationToken) ?? 0L;

        return committed + StagedCash(linkshellId);
    }

    private long StagedCash(int linkshellId) =>
        _db.ChangeTracker.Entries<JournalEntryLine>()
            .Where(tracked => tracked.State == EntityState.Added)
            .Select(tracked => tracked.Entity)
            .Where(line => line.LinkshellId == linkshellId
                && line.AccountNumber == TreasuryAccounts.GilOnHand
                // Drafts are not on the books, staged or otherwise.
                && JournalEntryStatuses.IsConfirmed(line.JournalEntry?.Status))
            .Sum(line => line.Amount);

    // ---- internals ----------------------------------------------------------------

    // The halves. Line 1 adds to a category, line 2 takes the same amount from another, so the
    // entry sums to zero by construction and INV-2 cannot be violated from here.
    //
    // A zero amount still produces both halves. Real zero-gil sales exist (both mark-sold paths clamp
    // a negative price to 0) and dropping the pair would lose which categories it would have used.
    //
    // A split replaces one of those halves with one line per member. The other half stays whole and
    // is ALWAYS line 1 — several reads take the first line as the entry's total, and that has to hold
    // whichever side of the pair the split lands on.
    //
    // A splittable kind with no recipients is recorded as the ordinary two halves rather than
    // rejected. Validation belongs to the callers, matching how a zero amount is handled: the writer
    // records what it is told, correctly.
    private async Task BuildHalvesAsync(
        JournalEntry entry,
        TreasuryTransactionKind kind,
        TreasuryEntryRequest request,
        CancellationToken cancellationToken)
    {
        var amount = Math.Abs(request.Amount);
        var accounts = await AccountsAsync(entry.LinkshellId, cancellationToken);
        var recipients = request.Recipients is { Count: > 0 } named ? named : null;

        if (recipients is null || kind.SplitAccount is not int splitAccount)
        {
            AddHalf(entry, accounts, kind.AddTo, amount, 1,
                request.CounterpartyAppUserId, request.CounterpartyCharacterName,
                request.HolderAppUserId, request.HolderCharacterName);
            AddHalf(entry, accounts, kind.TakeFrom, -amount, 2,
                request.CounterpartyAppUserId, request.CounterpartyCharacterName,
                request.HolderAppUserId, request.HolderCharacterName);
            return;
        }

        var wholeAccount = splitAccount == kind.AddTo ? kind.TakeFrom : kind.AddTo;
        var wholeSign = wholeAccount == kind.AddTo ? 1 : -1;

        // No member on the whole half: a lump sum belongs to everyone in the split, not to whoever
        // happens to sort first. The HOLDER still goes on it, and only on it — a split that moves
        // gil on hand does so as one lump off one mule, however many people the shares name.
        AddHalf(entry, accounts, wholeAccount, wholeSign * amount, 1, null, null,
            request.HolderAppUserId, request.HolderCharacterName);

        var lineNumber = 2;
        foreach (var (recipient, share) in TreasurySplit.Allocate(amount, recipients))
        {
            AddHalf(entry, accounts, splitAccount, -wholeSign * share, lineNumber++,
                recipient.AppUserId, recipient.CharacterName, null, null);
        }
    }

    private static void AddHalf(
        JournalEntry entry,
        Dictionary<int, LedgerAccount> accounts,
        int accountNumber,
        long amount,
        int lineNumber,
        string? counterpartyAppUserId,
        string? counterpartyCharacterName,
        string? holderAppUserId,
        string? holderCharacterName)
    {
        var account = accounts[accountNumber];
        entry.Lines.Add(new JournalEntryLine
        {
            JournalEntry = entry,
            LinkshellId = entry.LinkshellId,
            LedgerAccountId = account.Id,
            // Snapshots, so renaming a category never rewrites what an old entry says.
            AccountNumber = account.AccountNumber,
            AccountName = account.Name,
            Amount = amount,
            TransactionDate = entry.TransactionDate,
            LineNumber = lineNumber,
            LineMemo = null,
            // The member is recorded against the half that concerns them, not against gil on hand:
            // "who did we pay" belongs on the gil-paid-to-members side.
            CounterpartyAppUserId = accountNumber == TreasuryAccounts.GilOnHand
                ? null
                : counterpartyAppUserId,
            CounterpartyCharacterName = accountNumber == TreasuryAccounts.GilOnHand
                ? null
                : Trim(counterpartyCharacterName, 256),
            // And the exact complement: the holder goes on gil on hand and NOWHERE else. Anywhere
            // else it would be double-counted the moment anything sums by holder, because both
            // halves of an entry carry the same magnitude.
            HolderAppUserId = accountNumber == TreasuryAccounts.GilOnHand
                ? holderAppUserId
                : null,
            HolderCharacterName = accountNumber == TreasuryAccounts.GilOnHand
                ? Trim(holderCharacterName, 256)
                : null,
        });
    }

    // A mirror of an entry: same categories, opposite amounts. Built from the ORIGINAL'S OWN LINES
    // rather than from a re-derived amount, so a reversal always exactly undoes what was recorded even
    // if the catalog entry it came from has since changed.
    private async Task<JournalEntry> MirrorAsync(
        JournalEntry original,
        string reason,
        TreasuryActor actor,
        string kind,
        CancellationToken cancellationToken)
    {
        if (!JournalEntryStatuses.IsConfirmed(original.Status))
        {
            throw new ConfirmedTreasuryEntryException(original.EntryNumber);
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required to reverse or correct an entry.", nameof(reason));
        }
        await _periods.EnsureOpenAsync(original.LinkshellId, original.TransactionDate, cancellationToken);

        var lines = original.Lines.Count > 0
            ? original.Lines.ToList()
            : await _db.JournalEntryLines
                .Where(line => line.JournalEntryId == original.Id)
                .OrderBy(line => line.LineNumber)
                .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var sequence = await NextSequenceAsync(original.LinkshellId, cancellationToken);
        var mirror = new JournalEntry
        {
            LinkshellId = original.LinkshellId,
            LinkshellName = original.LinkshellName,
            Sequence = sequence,
            EntryNumber = FormatEntryNumber(sequence),
            // Dated to match the original, so reversing does not distort which period the movement
            // belongs to.
            TransactionDate = original.TransactionDate,
            Status = JournalEntryStatuses.Draft,
            Kind = kind,
            Source = original.Source,
            TransactionKind = original.TransactionKind,
            Memo = Trim(original.Memo, 1024),
            ReversesJournalEntryId = original.Id,
            CorrectionReason = Trim(reason, 512),
            CreatedAt = now,
            CreatedByAppUserId = actor.AppUserId,
            CreatedByCharacterName = actor.CharacterName,
            SourceItemId = original.SourceItemId,
            SourceAuctionItemId = original.SourceAuctionItemId,
            SourceAuctionHistoryId = original.SourceAuctionHistoryId,
        };

        foreach (var line in lines)
        {
            mirror.Lines.Add(new JournalEntryLine
            {
                JournalEntry = mirror,
                LinkshellId = line.LinkshellId,
                LedgerAccountId = line.LedgerAccountId,
                AccountNumber = line.AccountNumber,
                AccountName = line.AccountName,
                Amount = -line.Amount,
                TransactionDate = mirror.TransactionDate,
                LineNumber = line.LineNumber,
                LineMemo = line.LineMemo,
                CounterpartyAppUserId = line.CounterpartyAppUserId,
                CounterpartyCharacterName = line.CounterpartyCharacterName,
                // Copied, not cleared. A reversal has to take the gil back off the SAME mule it was
                // put on, or undoing a sale would leave the seller holding gil the treasury no
                // longer counts and push the difference into the unattributed bucket.
                HolderAppUserId = line.HolderAppUserId,
                HolderCharacterName = line.HolderCharacterName,
            });
        }

        _db.JournalEntries.Add(mirror);
        return await ConfirmAsync(mirror, actor, cancellationToken);
    }

    private async Task<Dictionary<int, LedgerAccount>> AccountsAsync(int linkshellId, CancellationToken cancellationToken)
    {
        if (_accountsByLinkshell.TryGetValue(linkshellId, out var cached))
        {
            return cached;
        }
        var accounts = await _accounts.EnsureAccountsAsync(linkshellId, cancellationToken);
        var byNumber = accounts.ToDictionary(account => account.AccountNumber);
        _accountsByLinkshell[linkshellId] = byNumber;
        return byNumber;
    }

    private async Task<int> NextSequenceAsync(int linkshellId, CancellationToken cancellationToken)
    {
        if (!_nextSequence.TryGetValue(linkshellId, out var next))
        {
            var max = await _db.JournalEntries
                .Where(entry => entry.LinkshellId == linkshellId)
                .Select(entry => (int?)entry.Sequence)
                .MaxAsync(cancellationToken);
            next = (max ?? 0) + 1;
        }
        _nextSequence[linkshellId] = next + 1;
        return next;
    }

    private static void RequireDraft(JournalEntry entry)
    {
        if (!JournalEntryStatuses.IsDraft(entry.Status))
        {
            throw new ConfirmedTreasuryEntryException(entry.EntryNumber);
        }
    }

    // Zero-padded so entries sort and read consistently, and so an officer can quote "#000142".
    private static string FormatEntryNumber(int sequence) => sequence.ToString("D6");

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
