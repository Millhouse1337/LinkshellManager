using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Paying members off the balance sheet's "who we owe" list.
//
// The property that matters most here is that a tick is NOT a new kind of movement: it records the
// same "We paid a member what we owed" transaction the Record form builds, so everything downstream
// — the balance sheet, Fix, Reverse — cannot tell the two apart. The rest of these cover what a
// batch does when the books have moved on since the page was drawn.
public class TreasurySettlementServiceTests
{
    private const int Linkshell = 1;

    private static readonly TreasuryActor Officer = new("user-1", "Millhouse");

    private static readonly IReadOnlyList<TreasuryRecipient> ThreeMembers = new[]
    {
        new TreasuryRecipient("user-a", "Ashira"),
        new TreasuryRecipient("user-m", "Millhouse"),
        new TreasuryRecipient(null, "Zeid"),   // unsynced: a real member with no account behind them
    };

    private static async Task<ApplicationDbContext> SeededContextAsync()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Linkshells.Add(new Linkshell { Id = Linkshell, LinkshellName = "Test LS" });
        await db.SaveChangesAsync();
        await new LedgerAccountProvisioner(db).EnsureAccountsAsync(Linkshell, CancellationToken.None);
        return db;
    }

    private static TreasuryJournalWriter NewWriter(ApplicationDbContext db) =>
        new(db, new LedgerAccountProvisioner(db), new LedgerPeriodGuard(db));

    private static TreasurySettlementService NewSettlements(ApplicationDbContext db) =>
        new(new TreasuryBalanceService(db), NewWriter(db));

    // Starting gil, then one obligation per member from a three-way owed split.
    private static async Task<ApplicationDbContext> OwingThreeMembersAsync(long startingGil, long owedTotal)
    {
        var db = await SeededContextAsync();
        var writer = NewWriter(db);
        await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(TreasuryTransactionKinds.StartingGil, startingGil, DateTime.UtcNow),
            Officer, CancellationToken.None);
        await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(
                TreasuryTransactionKinds.WeOweSeveralMembers, owedTotal, DateTime.UtcNow,
                Recipients: ThreeMembers),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Settle_PaysTheTickedMembersAndLeavesTheRestOwed()
    {
        using var db = await OwingThreeMembersAsync(5_000_000, 900_000);
        var settlements = NewSettlements(db);
        var balances = new TreasuryBalanceService(db);

        var result = await settlements.SettleAsync(
            Linkshell,
            new[]
            {
                new TreasurySettlementPick("Ashira", 300_000),
                new TreasurySettlementPick("Zeid", 300_000),
            },
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(2, result.Settled.Count);
        Assert.Empty(result.Skipped);
        Assert.Equal(600_000, result.TotalPaid);

        var snapshot = await balances.GetSnapshotAsync(Linkshell, null, null, CancellationToken.None);
        Assert.Equal(300_000, snapshot.WeOwe);
        Assert.Equal(4_400_000, snapshot.CashOnHand);
        // The books still add up after a batch, which is the whole point of going through the writer.
        Assert.True(snapshot.Balances);

        // The one member left is the one nobody ticked.
        var owed = (await balances.GetBalanceSheetAsync(Linkshell, null, null, CancellationToken.None)).OwedToMembers;
        Assert.Equal("Millhouse", Assert.Single(owed).CharacterName);
    }

    // Nothing about a settled member is deleted or flagged — their obligation simply nets to zero,
    // which is what takes them off the list.
    [Fact]
    public async Task Settle_RecordsTheSameTransactionTheFormWouldHave()
    {
        using var db = await OwingThreeMembersAsync(5_000_000, 900_000);
        var settlements = NewSettlements(db);

        await settlements.SettleAsync(
            Linkshell,
            new[] { new TreasurySettlementPick("Ashira", 300_000) },
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var entry = await db.JournalEntries
            .Include(item => item.Lines)
            .OrderByDescending(item => item.Sequence)
            .FirstAsync();

        Assert.Equal(TreasuryTransactionKinds.WePaidWhatWeOwed, entry.TransactionKind);
        Assert.Equal(JournalEntrySources.Manual, entry.Source);
        Assert.Equal(JournalEntryStatuses.Confirmed, entry.Status);
        // INV-2 holds for a batched payment exactly as it does for a typed one.
        Assert.Equal(0, entry.Lines.Sum(line => line.Amount));
        Assert.Equal(
            "Ashira",
            entry.Lines.Single(line => line.AccountNumber == TreasuryAccounts.WeOwe).CounterpartyCharacterName);
    }

    // Settling moves HOLDINGS only. Gil out was already counted when the debt was recorded — the
    // owed entry is the one that touches the gil-paid-to-members category — so counting it again on
    // payment would double it, and the "Adds up" chip would go false.
    //
    // What we're worth does not move either, and should not: handing over gil you already owed
    // swaps an obligation for cash of the same size.
    [Fact]
    public async Task Settle_MovesGilOnHandAndWhatWeOwe_ButNotGilOutOrNetWorth()
    {
        using var db = await OwingThreeMembersAsync(5_000_000, 900_000);
        var balances = new TreasuryBalanceService(db);
        var before = await balances.GetSnapshotAsync(Linkshell, null, null, CancellationToken.None);

        await NewSettlements(db).SettleAsync(
            Linkshell, new[] { new TreasurySettlementPick("Ashira", 300_000) },
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var after = await balances.GetSnapshotAsync(Linkshell, null, null, CancellationToken.None);

        // Unchanged: the flow figures and the bottom line.
        Assert.Equal(before.MoneyOut, after.MoneyOut);
        Assert.Equal(before.MoneyIn, after.MoneyIn);
        Assert.Equal(before.NetChange, after.NetChange);
        Assert.Equal(before.NetWorth, after.NetWorth);

        // Changed: the gil left, and the debt went with it.
        Assert.Equal(before.CashOnHand - 300_000, after.CashOnHand);
        Assert.Equal(before.WeOwe - 300_000, after.WeOwe);
        Assert.True(after.Balances);
    }

    // The other half of the pair above: recording the DEBT is what puts the gil in "gil out", even
    // though no gil has moved yet. Together these pin where the cost lands — once, on the way in.
    [Fact]
    public async Task OwingAMember_IsWhatCountsTowardGilOut()
    {
        using var db = await SeededContextAsync();
        var writer = NewWriter(db);
        var balances = new TreasuryBalanceService(db);

        await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(TreasuryTransactionKinds.StartingGil, 5_000_000, DateTime.UtcNow),
            Officer, CancellationToken.None);
        await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(
                TreasuryTransactionKinds.WeOweAMember, 300_000, DateTime.UtcNow,
                CounterpartyCharacterName: "Ashira"),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var snapshot = await balances.GetSnapshotAsync(Linkshell, null, null, CancellationToken.None);
        Assert.Equal(300_000, snapshot.MoneyOut);
        Assert.Equal(-300_000, snapshot.NetChange);
        // The gil itself has not moved yet.
        Assert.Equal(5_000_000, snapshot.CashOnHand);
        Assert.Equal(300_000, snapshot.WeOwe);
        Assert.True(snapshot.Balances);
    }

    // The reason every tick carries the figure it was showing: another officer recorded more gil
    // owed to Ashira while the page sat open, and paying "in full" now would hand over gil nobody
    // agreed to.
    [Fact]
    public async Task Settle_RefusesARowWhoseAmountMovedWhileThePageWasOpen()
    {
        using var db = await OwingThreeMembersAsync(5_000_000, 900_000);
        var writer = NewWriter(db);
        await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(
                TreasuryTransactionKinds.WeOweAMember, 200_000, DateTime.UtcNow,
                CounterpartyCharacterName: "Ashira"),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var settlements = NewSettlements(db);
        var result = await settlements.SettleAsync(
            Linkshell,
            new[] { new TreasurySettlementPick("Ashira", 300_000) },
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Empty(result.Settled);
        Assert.Equal("Ashira", Assert.Single(result.Skipped).CharacterName);

        var balances = new TreasuryBalanceService(db);
        var snapshot = await balances.GetSnapshotAsync(Linkshell, null, null, CancellationToken.None);
        Assert.Equal(5_000_000, snapshot.CashOnHand);
        Assert.Equal(1_100_000, snapshot.WeOwe);
    }

    // One member's figure moving must not cost the other members their payment: the batch reports
    // what it skipped and pays the rest.
    [Fact]
    public async Task Settle_PaysTheGoodRowsEvenWhenOneIsRefused()
    {
        using var db = await OwingThreeMembersAsync(5_000_000, 900_000);
        var settlements = NewSettlements(db);

        var result = await settlements.SettleAsync(
            Linkshell,
            new[]
            {
                new TreasurySettlementPick("Ashira", 999_999),   // stale
                new TreasurySettlementPick("Zeid", 300_000),     // current
            },
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal("Zeid", Assert.Single(result.Settled).CharacterName);
        Assert.Equal("Ashira", Assert.Single(result.Skipped).CharacterName);
        Assert.Equal(300_000, result.TotalPaid);
    }

    // Two people pressing the button on the same member, or one person double-tapping: the second
    // pass finds nothing owed and pays nothing rather than paying twice.
    [Fact]
    public async Task Settle_PaysAnAlreadySettledMemberNothing()
    {
        using var db = await OwingThreeMembersAsync(5_000_000, 900_000);
        var settlements = NewSettlements(db);

        await settlements.SettleAsync(
            Linkshell, new[] { new TreasurySettlementPick("Ashira", 300_000) },
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var second = await settlements.SettleAsync(
            Linkshell, new[] { new TreasurySettlementPick("Ashira", 300_000) },
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Empty(second.Settled);
        Assert.Single(second.Skipped);
        Assert.True(second.DidNothing);

        var balances = new TreasuryBalanceService(db);
        Assert.Equal(4_700_000, await balances.GetCashOnHandAsync(Linkshell, CancellationToken.None));
    }

    // The same name twice in one submission is one obligation, not two — names are what
    // ProjectByMember groups on.
    [Fact]
    public async Task Settle_PaysADuplicatedTickOnce()
    {
        using var db = await OwingThreeMembersAsync(5_000_000, 900_000);
        var settlements = NewSettlements(db);

        var result = await settlements.SettleAsync(
            Linkshell,
            new[]
            {
                new TreasurySettlementPick("Ashira", 300_000),
                new TreasurySettlementPick("ashira", 300_000),
            },
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Single(result.Settled);
        Assert.Equal(300_000, result.TotalPaid);
    }

    // Nobody can be paid off a name the linkshell owes nothing to, however the request got here.
    [Fact]
    public async Task Settle_PaysNothingToSomebodyNotOnTheList()
    {
        using var db = await OwingThreeMembersAsync(5_000_000, 900_000);
        var settlements = NewSettlements(db);

        var result = await settlements.SettleAsync(
            Linkshell, new[] { new TreasurySettlementPick("Nobody", 400_000) },
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Empty(result.Settled);
        Assert.Single(result.Skipped);
        Assert.Equal(5_000_000, await new TreasuryBalanceService(db)
            .GetCashOnHandAsync(Linkshell, CancellationToken.None));
    }

    // A locked period has to stop a batch the same way it stops a typed entry — and stop the WHOLE
    // batch, because the caller's single save is what makes a payout run atomic.
    [Fact]
    public async Task Settle_IsRefusedInsideALockedPeriod()
    {
        using var db = await OwingThreeMembersAsync(5_000_000, 900_000);
        db.LedgerPeriods.Add(new LedgerPeriod
        {
            LinkshellId = Linkshell,
            LockedThroughUtc = DateTime.UtcNow.AddDays(1),
            LockedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var settlements = NewSettlements(db);
        await Assert.ThrowsAsync<TreasuryPeriodLockedException>(() => settlements.SettleAsync(
            Linkshell, new[] { new TreasurySettlementPick("Ashira", 300_000) },
            Officer, CancellationToken.None));
    }

    // ---- the mirror panel: recording gil that has ARRIVED ------------------------------------
    //
    // Owed-to-us is settled the same way what-we-owe is, and it is the ONLY way it is settled: the
    // Record form has no pickable option for the arrival, because ticking a name off a list the app
    // already knows beats typing the name and the figure back in.

    // Two debts owed TO the linkshell, from parties who are usually not members at all.
    private static async Task<ApplicationDbContext> OwedByTwoPartiesAsync(long startingGil)
    {
        var db = await SeededContextAsync();
        var writer = NewWriter(db);
        await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(TreasuryTransactionKinds.StartingGil, startingGil, DateTime.UtcNow),
            Officer, CancellationToken.None);
        foreach (var (who, amount) in new[] { ("Wyvern LS", 400_000L), ("Skid", 250_000L) })
        {
            await writer.RecordAsync(
                Linkshell,
                new TreasuryEntryRequest(
                    TreasuryTransactionKinds.SomeoneOwesUsForWork, amount, DateTime.UtcNow,
                    CounterpartyCharacterName: who),
                Officer, CancellationToken.None);
        }
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task SettleOwedToUs_TakesTheGilInAndLeavesTheRestOutstanding()
    {
        using var db = await OwedByTwoPartiesAsync(5_000_000);
        var settlements = NewSettlements(db);
        var balances = new TreasuryBalanceService(db);

        var result = await settlements.SettleAsync(
            Linkshell,
            new[] { new TreasurySettlementPick("Wyvern LS", 400_000) },
            TreasurySettlementDirection.TheyPaidUs,
            // Whose mule the arriving gil lands on. The panel requires it; the service records it.
            new TreasuryHolder(null, "Millhouse"),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Empty(result.Skipped);
        Assert.Equal(400_000, result.TotalPaid);
        // Reported as an arrival, not a payout. The same words in both directions is how a treasury
        // ends up read backwards.
        Assert.Contains("from", result.Message, StringComparison.OrdinalIgnoreCase);

        // Gil on hand goes UP — the opposite of what paying a member does — and the debt is cleared.
        var snapshot = await balances.GetSnapshotAsync(Linkshell, null, null, CancellationToken.None);
        Assert.Equal(5_400_000, snapshot.CashOnHand);
        Assert.Equal(250_000, snapshot.OwedToUs);
        Assert.True(snapshot.Balances);

        // Whoever paid drops off the list; the other one stays, exactly like the other panel.
        var sheet = await balances.GetBalanceSheetAsync(Linkshell, null, null, CancellationToken.None);
        var left = Assert.Single(sheet.OwedToUsBy);
        Assert.Equal("Skid", left.CharacterName);
        Assert.Equal(250_000, left.Amount);
        // And nothing leaked into the other half of the sheet.
        Assert.Empty(sheet.OwedToMembers);
    }

    // The stale-figure guard, on the other side. A page left open while someone records more owed to
    // us must not take in the newer, larger figure — the same rule, and the same skip, as paying out.
    [Fact]
    public async Task SettleOwedToUs_SkipsARowWhoseFigureMovedWhileThePageWasOpen()
    {
        using var db = await OwedByTwoPartiesAsync(5_000_000);
        var settlements = NewSettlements(db);
        var balances = new TreasuryBalanceService(db);

        var result = await settlements.SettleAsync(
            Linkshell,
            // The screen said 400,000; the books say 400,000 too, but this tick claims the older figure.
            new[] { new TreasurySettlementPick("Wyvern LS", 100_000) },
            TreasurySettlementDirection.TheyPaidUs,
            // Whose mule the arriving gil lands on. The panel requires it; the service records it.
            new TreasuryHolder(null, "Millhouse"),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Empty(result.Settled);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal("Wyvern LS", skip.CharacterName);

        // Nothing moved.
        var snapshot = await balances.GetSnapshotAsync(Linkshell, null, null, CancellationToken.None);
        Assert.Equal(5_000_000, snapshot.CashOnHand);
        Assert.Equal(650_000, snapshot.OwedToUs);
    }

    // The two directions must not see each other's names. A member the linkshell owes is not someone
    // who owes it, and ticking one list must never settle a row on the other.
    [Fact]
    public async Task TheTwoHalvesOfTheSheetAreSeparateLists()
    {
        using var db = await OwingThreeMembersAsync(5_000_000, 900_000);
        var writer = NewWriter(db);
        await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(
                TreasuryTransactionKinds.SomeoneOwesUsForWork, 400_000, DateTime.UtcNow,
                // Deliberately a name that is ALSO on the who-we-owe list.
                CounterpartyCharacterName: "Ashira"),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var settlements = NewSettlements(db);
        var balances = new TreasuryBalanceService(db);

        // Ticking Ashira on the owed-to-us side clears what she OWES, and leaves what she is owed.
        var result = await settlements.SettleAsync(
            Linkshell,
            new[] { new TreasurySettlementPick("Ashira", 400_000) },
            TreasurySettlementDirection.TheyPaidUs,
            // Whose mule the arriving gil lands on. The panel requires it; the service records it.
            new TreasuryHolder(null, "Millhouse"),
            Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(400_000, result.TotalPaid);

        var sheet = await balances.GetBalanceSheetAsync(Linkshell, null, null, CancellationToken.None);
        Assert.Empty(sheet.OwedToUsBy);
        Assert.Equal(300_000, Assert.Single(sheet.OwedToMembers, owed => owed.CharacterName == "Ashira").Amount);
        Assert.True(sheet.Snapshot.Balances);
    }
}
