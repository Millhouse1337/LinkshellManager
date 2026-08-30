using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Covers the single choke-point every gil movement goes through.
//
// The two things that must hold no matter what a caller does:
//   INV-2  an entry's halves always sum to zero, which is what makes gil on hand provable
//   INV-3  a confirmed entry is never edited; it is reversed or corrected
public class TreasuryJournalWriterTests
{
    private const int Linkshell = 1;

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static TreasuryJournalWriter NewWriter(ApplicationDbContext db) =>
        new(db, new LedgerAccountProvisioner(db), new LedgerPeriodGuard(db));

    private static readonly TreasuryActor Officer = new("user-1", "Millhouse");

    private static async Task<ApplicationDbContext> SeededContextAsync()
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = Linkshell, LinkshellName = "Test LS" });
        await db.SaveChangesAsync();
        await new LedgerAccountProvisioner(db).EnsureAccountsAsync(Linkshell, CancellationToken.None);
        return db;
    }

    private static TreasuryEntryRequest Sale(long amount, DateTime? on = null) =>
        new(TreasuryTransactionKinds.SoldAnItem, amount, on ?? new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Draft_BuildsTwoHalvesThatSumToZero()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.DraftAsync(Linkshell, Sale(4_000_000), Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(0, entry.Lines.Sum(line => line.Amount));
        Assert.Equal(JournalEntryStatuses.Draft, entry.Status);
        // Gil on hand goes UP, the item-sales category takes the other side.
        Assert.Equal(4_000_000, entry.Lines.Single(l => l.AccountNumber == TreasuryAccounts.GilOnHand).Amount);
        Assert.Equal(-4_000_000, entry.Lines.Single(l => l.AccountNumber == TreasuryAccounts.ItemSales).Amount);
    }

    // The holder is the exact complement of the counterparty: it goes on gil on hand and NOWHERE
    // else. Both halves of an entry carry the same magnitude, so a holder on the second half too
    // would double every arrival the moment anything sums by holder.
    [Fact]
    public async Task Draft_PutsTheHolderOnTheGilOnHandHalfAndNowhereElse()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.DraftAsync(
            Linkshell,
            Sale(4_000_000) with { HolderAppUserId = "user-9", HolderCharacterName = "Edicius" },
            Officer,
            CancellationToken.None);
        await db.SaveChangesAsync();

        var cash = entry.Lines.Single(line => line.AccountNumber == TreasuryAccounts.GilOnHand);
        var other = entry.Lines.Single(line => line.AccountNumber == TreasuryAccounts.ItemSales);
        Assert.Equal("Edicius", cash.HolderCharacterName);
        Assert.Equal("user-9", cash.HolderAppUserId);
        Assert.Null(other.HolderCharacterName);
        Assert.Null(other.HolderAppUserId);
    }

    // Undoing a sale has to take the gil back off the mule it was put on, or the seller is left
    // holding gil the treasury no longer counts and the difference lands in the unattributed bucket.
    [Fact]
    public async Task Reverse_TakesTheGilBackOffTheSameMule()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var sale = await writer.RecordAsync(
            Linkshell,
            Sale(4_000_000) with { HolderCharacterName = "Edicius" },
            Officer,
            CancellationToken.None);
        await db.SaveChangesAsync();

        var reversal = await writer.ReverseAsync(sale, "Sold in error.", Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var cash = reversal.Lines.Single(line => line.AccountNumber == TreasuryAccounts.GilOnHand);
        Assert.Equal("Edicius", cash.HolderCharacterName);
        Assert.Equal(-4_000_000, cash.Amount);
    }

    // A draft is a scratch pad — it must NOT count toward the balance.
    [Fact]
    public async Task Draft_DoesNotMoveTheBalanceUntilConfirmed()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);
        var balances = new TreasuryBalanceService(db);

        var entry = await writer.DraftAsync(Linkshell, Sale(1_000_000), Officer, CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Equal(0, await balances.GetCashOnHandAsync(Linkshell, CancellationToken.None));

        await writer.ConfirmAsync(entry, Officer, CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Equal(1_000_000, await balances.GetCashOnHandAsync(Linkshell, CancellationToken.None));
    }

    [Fact]
    public async Task Confirm_StampsWhoAndWhenAndIsIdempotent()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.DraftAsync(Linkshell, Sale(500), Officer, CancellationToken.None);
        await writer.ConfirmAsync(entry, Officer, CancellationToken.None);
        var firstConfirmedAt = entry.ConfirmedAt;

        // A double-tap on the Confirm button is not an error.
        await writer.ConfirmAsync(entry, new TreasuryActor("someone-else", "Other"), CancellationToken.None);

        Assert.Equal(JournalEntryStatuses.Confirmed, entry.Status);
        Assert.Equal(firstConfirmedAt, entry.ConfirmedAt);
        Assert.Equal("Millhouse", entry.ConfirmedByCharacterName);
    }

    [Fact]
    public async Task UpdateDraft_RebuildsTheHalves()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.DraftAsync(Linkshell, Sale(1_000), Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        await writer.UpdateDraftAsync(
            entry,
            new TreasuryEntryRequest(
                TreasuryTransactionKinds.GotADonation, 2_500, entry.TransactionDate, Memo: "from Bob"),
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(0, entry.Lines.Sum(line => line.Amount));
        Assert.Equal(TreasuryTransactionKinds.GotADonation, entry.TransactionKind);
        Assert.Equal(2_500, entry.Lines.Single(l => l.AccountNumber == TreasuryAccounts.GilOnHand).Amount);
        Assert.Contains(entry.Lines, l => l.AccountNumber == TreasuryAccounts.MemberDonations);
        Assert.DoesNotContain(entry.Lines, l => l.AccountNumber == TreasuryAccounts.ItemSales);
    }

    // INV-3, from the caller's side: once it is on the books it cannot be edited or thrown away.
    [Fact]
    public async Task Confirmed_CannotBeEditedOrDiscarded()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.DraftAsync(Linkshell, Sale(1_000), Officer, CancellationToken.None);
        await writer.ConfirmAsync(entry, Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConfirmedTreasuryEntryException>(() =>
            writer.UpdateDraftAsync(entry, Sale(2_000), CancellationToken.None));
        Assert.Throws<ConfirmedTreasuryEntryException>(() => writer.DiscardDraft(entry));
    }

    [Fact]
    public async Task DiscardDraft_RemovesTheDraftAndItsHalves()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.DraftAsync(Linkshell, Sale(1_000), Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        writer.DiscardDraft(entry);
        await db.SaveChangesAsync();

        Assert.Empty(db.JournalEntries);
        Assert.Empty(db.JournalEntryLines);
    }

    [Fact]
    public async Task Reverse_NetsTheBalanceToZeroAndKeepsBothEntries()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);
        var balances = new TreasuryBalanceService(db);

        var sale = await writer.RecordAsync(Linkshell, Sale(8_000_000), Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var reversal = await writer.ReverseAsync(sale, "Sold at the wrong price.", Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(0, await balances.GetCashOnHandAsync(Linkshell, CancellationToken.None));
        // The original survives. That is the whole point — a delete could only ever say it never
        // happened, which is not what occurred.
        Assert.Equal(2, await db.JournalEntries.CountAsync());
        Assert.Equal(sale.Id, reversal.ReversesJournalEntryId);
        Assert.Equal(JournalEntryKinds.Reversal, reversal.Kind);
        Assert.Equal("Sold at the wrong price.", reversal.CorrectionReason);
        // Dated to match the original, so reversing does not shift which period the movement lands in.
        Assert.Equal(sale.TransactionDate, reversal.TransactionDate);
    }

    [Fact]
    public async Task Reverse_RequiresAReason()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var sale = await writer.RecordAsync(Linkshell, Sale(1_000), Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            writer.ReverseAsync(sale, "   ", Officer, CancellationToken.None));
    }

    [Fact]
    public async Task Reverse_RejectsADraft()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var draft = await writer.DraftAsync(Linkshell, Sale(1_000), Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConfirmedTreasuryEntryException>(() =>
            writer.ReverseAsync(draft, "nope", Officer, CancellationToken.None));
    }

    // "Fix" is one action for the officer: fat-fingering 40,000,000 five seconds after confirming
    // 4,000,000 must leave the balance at the corrected amount.
    [Fact]
    public async Task Correct_LeavesTheCorrectedAmountAndAVisibleTrail()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);
        var balances = new TreasuryBalanceService(db);

        var wrong = await writer.RecordAsync(Linkshell, Sale(40_000_000), Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var (reversal, replacement) = await writer.CorrectAsync(
            wrong, Sale(4_000_000), "Typed an extra zero.", Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(4_000_000, await balances.GetCashOnHandAsync(Linkshell, CancellationToken.None));
        var snapshot = await balances.GetSnapshotAsync(Linkshell, null, null, CancellationToken.None);
        Assert.Equal(4_000_000, snapshot.MoneyIn);
        Assert.Equal(0, snapshot.MoneyOut);
        Assert.True(snapshot.Balances);

        Assert.Equal(3, await db.JournalEntries.CountAsync());
        Assert.Equal(JournalEntryKinds.Reversal, reversal.Kind);
        Assert.Equal(JournalEntryKinds.Correction, replacement.Kind);
        Assert.Equal(wrong.Id, replacement.ReversesJournalEntryId);
    }

    // Entry numbers are what an officer quotes, so they must never collide — including for several
    // entries written in one request, which is what an auction close does.
    [Fact]
    public async Task Sequence_AndEntryNumberAreUniqueAcrossOneRequest()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        for (var n = 0; n < 25; n++)
        {
            await writer.RecordAsync(Linkshell, Sale(1_000 + n), Officer, CancellationToken.None);
        }
        await db.SaveChangesAsync();

        var entries = await db.JournalEntries.ToListAsync();
        Assert.Equal(25, entries.Select(entry => entry.Sequence).Distinct().Count());
        Assert.Equal(25, entries.Select(entry => entry.EntryNumber).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 25), entries.Select(e => e.Sequence).OrderBy(s => s));
        Assert.Equal("000001", entries.OrderBy(e => e.Sequence).First().EntryNumber);
    }

    [Fact]
    public async Task Sequence_ContinuesFromWhatIsAlreadyStored()
    {
        using var db = await SeededContextAsync();

        await NewWriter(db).RecordAsync(Linkshell, Sale(1), Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        // A fresh writer, i.e. the next request.
        var second = await NewWriter(db).RecordAsync(Linkshell, Sale(2), Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(2, second.Sequence);
        Assert.Equal("000002", second.EntryNumber);
    }

    // The staged-cash case: closing a gil auction settles several items in ONE save, and the second
    // item has to see the first item's payout or the solvency check reads a stale balance.
    [Fact]
    public async Task GetCashOnHand_SeesEntriesStagedButNotYetSaved()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        await writer.RecordAsync(Linkshell, Sale(1_000_000), Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        // Two payouts staged in this request, nothing saved yet.
        var payout = new TreasuryEntryRequest(
            TreasuryTransactionKinds.PaidGilToMember, 300_000, DateTime.UtcNow);
        await writer.RecordAsync(Linkshell, payout, Officer, CancellationToken.None);
        await writer.RecordAsync(Linkshell, payout, Officer, CancellationToken.None);

        Assert.Equal(400_000, await writer.GetCashOnHandAsync(Linkshell, CancellationToken.None));
    }

    [Fact]
    public async Task Record_RejectsAnUnknownKind()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        await Assert.ThrowsAsync<UnknownTreasuryTransactionKindException>(() =>
            writer.RecordAsync(
                Linkshell,
                new TreasuryEntryRequest("SoldMyHouse", 1_000, DateTime.UtcNow),
                Officer,
                CancellationToken.None));
    }

    // The amount is a magnitude; direction belongs to the categories. A caller passing a negative must
    // not be able to invert an entry.
    [Fact]
    public async Task Record_TreatsANegativeAmountAsAMagnitude()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(TreasuryTransactionKinds.SoldAnItem, -5_000, DateTime.UtcNow),
            Officer,
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(5_000, entry.Lines.Single(l => l.AccountNumber == TreasuryAccounts.GilOnHand).Amount);
        Assert.Equal(0, entry.Lines.Sum(line => line.Amount));
    }

    // The member goes on the half that concerns them, not on gil on hand: "who did we pay" belongs to
    // the gil-paid-to-members side.
    [Fact]
    public async Task Record_PutsTheMemberOnTheCategoryHalf()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(
                TreasuryTransactionKinds.PaidGilToMember, 800_000, DateTime.UtcNow,
                CounterpartyAppUserId: "user-9", CounterpartyCharacterName: "Winner"),
            Officer,
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(
            "Winner",
            entry.Lines.Single(l => l.AccountNumber == TreasuryAccounts.GilToMembers).CounterpartyCharacterName);
        Assert.Null(
            entry.Lines.Single(l => l.AccountNumber == TreasuryAccounts.GilOnHand).CounterpartyCharacterName);
    }

    // Snapshots, so renaming a category never rewrites what an old entry says.
    [Fact]
    public async Task Record_SnapshotsTheCategoryNameAtTheTime()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.RecordAsync(Linkshell, Sale(1_000), Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var renamed = await db.LedgerAccounts.SingleAsync(a => a.AccountNumber == TreasuryAccounts.ItemSales);
        renamed.Name = "Auction house takings";
        await db.SaveChangesAsync();

        var line = entry.Lines.Single(l => l.AccountNumber == TreasuryAccounts.ItemSales);
        Assert.Equal("Item sales", line.AccountName);
    }

    // Every "what happened" option in the catalog must produce a balanced pair against real categories.
    [Fact]
    public async Task Record_EveryTransactionKindProducesABalancedEntry()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        foreach (var kind in TreasuryTransactionKinds.All)
        {
            var entry = await writer.RecordAsync(
                Linkshell,
                new TreasuryEntryRequest(kind.Key, 1_234, DateTime.UtcNow),
                Officer,
                CancellationToken.None);

            Assert.Equal(2, entry.Lines.Count);
            Assert.Equal(0, entry.Lines.Sum(line => line.Amount));
            Assert.Equal(kind.EntryKind, entry.Kind);
        }
        await db.SaveChangesAsync();
    }

    // --- splits: one lump sum shared between several members ---

    private static readonly IReadOnlyList<TreasuryRecipient> ThreeMembers = new[]
    {
        new TreasuryRecipient("user-a", "Ashira"),
        new TreasuryRecipient("user-m", "Millhouse"),
        new TreasuryRecipient(null, "Zeid"),   // unsynced: a real member with no account behind them
    };

    private static TreasuryEntryRequest Split(
        string kind, long amount, IReadOnlyList<TreasuryRecipient>? members = null) =>
        new(kind, amount, new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc),
            Recipients: members ?? ThreeMembers);

    [Fact]
    public async Task Draft_SplitsAcrossEveryMemberAndStillSumsToZero()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.DraftAsync(
            Linkshell, Split(TreasuryTransactionKinds.SplitGilAmongMembers, 1_000_000),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(4, entry.Lines.Count);
        Assert.Equal(0, entry.Lines.Sum(line => line.Amount));
        Assert.Equal(
            -1_000_000,
            entry.Lines.Single(l => l.AccountNumber == TreasuryAccounts.GilOnHand).Amount);
        Assert.Equal(
            new[] { 333_334L, 333_333L, 333_333L },
            entry.Lines.Where(l => l.AccountNumber == TreasuryAccounts.GilToMembers)
                .OrderBy(l => l.LineNumber).Select(l => l.Amount).ToArray());
    }

    // Several reads take the first line as the entry's total. That has to hold whichever side of the
    // pair the split lands on — and the two split kinds land on opposite sides.
    [Fact]
    public async Task Draft_PutsTheWholeAmountOnLineOneForBothSplitKinds()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        foreach (var kind in TreasuryTransactionKinds.All.Where(k => k.IsSplittable))
        {
            var entry = await writer.DraftAsync(
                Linkshell, Split(kind.Key, 1_000_000), Officer, CancellationToken.None);

            var first = entry.Lines.Single(line => line.LineNumber == 1);
            Assert.Equal(1_000_000, Math.Abs(first.Amount));
            Assert.NotEqual(kind.SplitAccount, first.AccountNumber);
            Assert.Equal(4, entry.Lines.Count);
        }
        await db.SaveChangesAsync();
    }

    // Who was paid what has to survive on the line, or a split is just a number with no names.
    [Fact]
    public async Task Draft_NamesEachMemberOnTheirOwnLineAndNobodyOnTheWholeAmount()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.DraftAsync(
            Linkshell, Split(TreasuryTransactionKinds.SplitGilAmongMembers, 900_000),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Null(entry.Lines.Single(l => l.LineNumber == 1).CounterpartyCharacterName);
        Assert.Equal(
            new[] { "Ashira", "Millhouse", "Zeid" },
            entry.Lines.Where(l => l.LineNumber > 1)
                .OrderBy(l => l.LineNumber).Select(l => l.CounterpartyCharacterName).ToArray());
        // The unsynced member is recorded by name with no account, not dropped.
        var unsynced = entry.Lines.Single(l => l.CounterpartyCharacterName == "Zeid");
        Assert.Null(unsynced.CounterpartyAppUserId);
        Assert.Equal(300_000, unsynced.Amount);
    }

    // Every existing caller passes no recipients, so every existing caller must be untouched.
    [Fact]
    public async Task Draft_WithoutMembersFallsBackToThePlainTwoHalves()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.DraftAsync(
            Linkshell,
            new TreasuryEntryRequest(
                TreasuryTransactionKinds.SplitGilAmongMembers, 900_000, DateTime.UtcNow),
            Officer,
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(0, entry.Lines.Sum(line => line.Amount));
    }

    [Fact]
    public async Task Draft_IgnoresMembersForAKindThatDoesNotSplit()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        var entry = await writer.DraftAsync(
            Linkshell, Split(TreasuryTransactionKinds.SoldAnItem, 900_000),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(2, entry.Lines.Count);
        Assert.Equal(0, entry.Lines.Sum(line => line.Amount));
    }

    // A reversal is built from the original's own lines, so it should undo a split of any width
    // without knowing anything about splits.
    [Fact]
    public async Task Reverse_UndoesEveryLineOfASplit()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);
        var balances = new TreasuryBalanceService(db);

        var original = await writer.RecordAsync(
            Linkshell, Split(TreasuryTransactionKinds.SplitGilAmongMembers, 1_000_000),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var reversal = await writer.ReverseAsync(original, "Wrong group", Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(4, reversal.Lines.Count);
        Assert.Equal(0, reversal.Lines.Sum(line => line.Amount));
        Assert.Equal(
            new[] { "Ashira", "Millhouse", "Zeid" },
            reversal.Lines.Where(l => l.CounterpartyCharacterName is not null)
                .Select(l => l.CounterpartyCharacterName!).OrderBy(name => name).ToArray());
        Assert.Equal(0, await balances.GetCashOnHandAsync(Linkshell, CancellationToken.None));
    }

    // Fixing a split has to replace the WHOLE split. Reversing 1,000,000 and re-recording it to one
    // person would balance perfectly and lose most of the payout.
    [Fact]
    public async Task Correct_ReplacesTheWholeSplitNotJustTheFirstMember()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);
        var balances = new TreasuryBalanceService(db);

        var original = await writer.RecordAsync(
            Linkshell, Split(TreasuryTransactionKinds.SplitGilAmongMembers, 1_000_000),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var four = ThreeMembers.Append(new TreasuryRecipient("user-b", "Bruno")).ToList();
        var (_, replacement) = await writer.CorrectAsync(
            original,
            Split(TreasuryTransactionKinds.SplitGilAmongMembers, 900_000, four),
            "Bruno was on the claim too",
            Officer,
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(5, replacement.Lines.Count);
        Assert.Equal(4, replacement.Lines.Count(l => l.CounterpartyCharacterName is not null));
        Assert.All(
            replacement.Lines.Where(l => l.AccountNumber == TreasuryAccounts.GilToMembers),
            line => Assert.Equal(225_000, line.Amount));
        Assert.Equal(-900_000, await balances.GetCashOnHandAsync(Linkshell, CancellationToken.None));
    }

    // The owed half of the pair: everyone's share is recorded, but no gil has moved yet.
    [Fact]
    public async Task OwedSplit_RecordsWhatEachMemberIsDueWithoutMovingGil()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);
        var balances = new TreasuryBalanceService(db);

        var entry = await writer.RecordAsync(
            Linkshell, Split(TreasuryTransactionKinds.WeOweSeveralMembers, 900_000),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.DoesNotContain(entry.Lines, line => line.AccountNumber == TreasuryAccounts.GilOnHand);
        Assert.Equal(0, await balances.GetCashOnHandAsync(Linkshell, CancellationToken.None));

        var snapshot = await balances.GetSnapshotAsync(Linkshell, null, null, CancellationToken.None);
        Assert.Equal(900_000, snapshot.WeOwe);
        Assert.True(snapshot.Balances);
    }

    // The reason the owed split divides what-we-owe rather than the gil-paid side: each member's
    // share has to be settleable on its own, whenever that person is next online.
    [Fact]
    public async Task WePaidWhatWeOwed_SettlesOneMemberOfAnEarlierOwedSplit()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);
        var balances = new TreasuryBalanceService(db);

        await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(TreasuryTransactionKinds.StartingGil, 5_000_000, DateTime.UtcNow),
            Officer, CancellationToken.None);
        await writer.RecordAsync(
            Linkshell, Split(TreasuryTransactionKinds.WeOweSeveralMembers, 900_000),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(
                TreasuryTransactionKinds.WePaidWhatWeOwed, 300_000, DateTime.UtcNow,
                CounterpartyCharacterName: "Ashira"),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var snapshot = await balances.GetSnapshotAsync(Linkshell, null, null, CancellationToken.None);
        Assert.Equal(600_000, snapshot.WeOwe);
        Assert.Equal(4_700_000, snapshot.CashOnHand);
        Assert.True(snapshot.Balances);
    }

    // The evidence for retiring "gil owed to one member": a Split Gil with a single person picked
    // produces the same obligation, on the same category, under the same name — so the picker loses
    // an option and nobody loses a capability.
    [Fact]
    public async Task SplitOfOne_RecordsTheSameObligationTheRetiredSingleKindDid()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);
        var balances = new TreasuryBalanceService(db);

        await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(TreasuryTransactionKinds.StartingGil, 5_000_000, DateTime.UtcNow),
            Officer, CancellationToken.None);
        var entry = await writer.RecordAsync(
            Linkshell,
            Split(
                TreasuryTransactionKinds.WeOweSeveralMembers,
                300_000,
                new[] { new TreasuryRecipient("user-a", "Ashira") }),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        // One name, on the what-we-owe half, for the whole amount.
        var owedLine = Assert.Single(entry.Lines.Where(line => line.AccountNumber == TreasuryAccounts.WeOwe));
        Assert.Equal("Ashira", owedLine.CounterpartyCharacterName);
        Assert.Equal(-300_000, owedLine.Amount);
        Assert.Equal(0, entry.Lines.Sum(line => line.Amount));
        // Gil on hand is untouched — the point of an obligation.
        Assert.DoesNotContain(entry.Lines, line => line.AccountNumber == TreasuryAccounts.GilOnHand);

        // And they show up on the list the tick-and-pay panel reads.
        var sheet = await balances.GetBalanceSheetAsync(Linkshell, null, null, CancellationToken.None);
        var snapshot = sheet.Snapshot;
        var owed = sheet.OwedToMembers;
        Assert.Equal(300_000, snapshot.WeOwe);
        Assert.Equal(5_000_000, snapshot.CashOnHand);
        var obligation = Assert.Single(owed);
        Assert.Equal("Ashira", obligation.CharacterName);
        Assert.Equal(300_000, obligation.Amount);
    }

    // Retiring a kind is a PICKER decision, not a writer one. The writer must still record one,
    // because a Fix on an entry already recorded under it rebuilds the entry from that same kind.
    [Fact]
    public async Task Writer_StillRecordsARetiredKind_SoFixesCanReproduceThem()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);

        foreach (var kind in TreasuryTransactionKinds.All.Where(kind => kind.IsRetired))
        {
            var entry = await writer.RecordAsync(
                Linkshell,
                new TreasuryEntryRequest(
                    kind.Key, 100_000, DateTime.UtcNow, CounterpartyCharacterName: "Ashira"),
                Officer, CancellationToken.None);
            Assert.Equal(0, entry.Lines.Sum(line => line.Amount));
            Assert.Equal(kind.Key, entry.TransactionKind);
        }
        await db.SaveChangesAsync();
    }
}
