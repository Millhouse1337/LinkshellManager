using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LinkshellManager.Tests;

// A treasury entry is reversed at most once. A second reversal took the same gil off the books a
// second time: gil on hand ended up short by the entry's amount.
public class TreasuryOneReversalTests
{
    private const int Ls = 1;
    private static readonly TreasuryActor Officer = new("user-1", "Millhouse");

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // ItemSaleRecorder opens a transaction; the in-memory provider has none.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static TreasuryJournalWriter NewWriter(ApplicationDbContext db) =>
        new(db, new LedgerAccountProvisioner(db), new LedgerPeriodGuard(db));

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "Test LS" });
        await db.SaveChangesAsync();
        await new LedgerAccountProvisioner(db).EnsureAccountsAsync(Ls, CancellationToken.None);
        return db;
    }

    private static TreasuryEntryRequest Sale(long amount) =>
        new(TreasuryTransactionKinds.SoldAnItem, amount, new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc));

    private static async Task<JournalEntry> RecordedSaleAsync(ApplicationDbContext db, long amount)
    {
        var sale = await NewWriter(db).RecordAsync(Ls, Sale(amount), Officer, CancellationToken.None);
        await db.SaveChangesAsync();
        return sale;
    }

    // ---- what stays the same ----

    [Fact]
    public async Task FirstReversal_IsRecorded_AndUndoesTheEntry()
    {
        await using var db = await SeededAsync();
        var sale = await RecordedSaleAsync(db, 1_000);
        var writer = NewWriter(db);

        await writer.ReverseAsync(sale, "Sold in error.", Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(0, await writer.GetCashOnHandAsync(Ls, CancellationToken.None));
        Assert.True(await writer.HasReversalAsync(sale, CancellationToken.None));
    }

    // ---- what changes: the second one is refused ----

    [Fact]
    public async Task SecondReversal_IsRefused_AndMovesNoGil()
    {
        await using var db = await SeededAsync();
        var sale = await RecordedSaleAsync(db, 1_000);
        await NewWriter(db).ReverseAsync(sale, "Sold in error.", Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var writer = NewWriter(db);
        await Assert.ThrowsAsync<AlreadyReversedTreasuryEntryException>(() =>
            writer.ReverseAsync(sale, "Double click.", Officer, CancellationToken.None));
        await db.SaveChangesAsync();

        Assert.Equal(0, await writer.GetCashOnHandAsync(Ls, CancellationToken.None));
        Assert.Equal(1, await db.JournalEntries.CountAsync(e => e.Kind == JournalEntryKinds.Reversal));
    }

    // Two reversals staged in one request, before either is saved.
    [Fact]
    public async Task SecondReversalInTheSameRequest_IsRefused()
    {
        await using var db = await SeededAsync();
        var sale = await RecordedSaleAsync(db, 1_000);
        var writer = NewWriter(db);

        await writer.ReverseAsync(sale, "Sold in error.", Officer, CancellationToken.None);
        await Assert.ThrowsAsync<AlreadyReversedTreasuryEntryException>(() =>
            writer.ReverseAsync(sale, "Again.", Officer, CancellationToken.None));
    }

    // A Fix is a reversal plus a replacement, so the same rule covers Fix after Reverse, Reverse
    // after Fix, and Fix twice.
    [Fact]
    public async Task FixAfterReverse_IsRefused()
    {
        await using var db = await SeededAsync();
        var sale = await RecordedSaleAsync(db, 1_000);
        await NewWriter(db).ReverseAsync(sale, "Sold in error.", Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<AlreadyReversedTreasuryEntryException>(() =>
            NewWriter(db).CorrectAsync(sale, Sale(900), "Wrong price.", Officer, CancellationToken.None));
    }

    [Fact]
    public async Task ReverseAfterFix_IsRefused()
    {
        await using var db = await SeededAsync();
        var sale = await RecordedSaleAsync(db, 1_000);
        await NewWriter(db).CorrectAsync(sale, Sale(900), "Wrong price.", Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var writer = NewWriter(db);
        await Assert.ThrowsAsync<AlreadyReversedTreasuryEntryException>(() =>
            writer.ReverseAsync(sale, "Undo.", Officer, CancellationToken.None));
        Assert.Equal(900, await writer.GetCashOnHandAsync(Ls, CancellationToken.None));
    }

    // Reversing the reversal (putting the sale back) is still allowed once: it is a different entry.
    [Fact]
    public async Task ReversingTheReversal_IsStillAllowed()
    {
        await using var db = await SeededAsync();
        var sale = await RecordedSaleAsync(db, 1_000);
        var reversal = await NewWriter(db).ReverseAsync(sale, "Sold in error.", Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        var writer = NewWriter(db);
        await writer.ReverseAsync(reversal, "It was sold after all.", Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(1_000, await writer.GetCashOnHandAsync(Ls, CancellationToken.None));
    }

    // Undoing a sale whose entry an officer already reversed from the treasury list: the gil is
    // already off the books, so only the item goes back to unsold.
    [Fact]
    public async Task UnsellAfterTheEntryWasReversed_DoesNotTakeTheGilOffAgain()
    {
        await using var db = await SeededAsync();
        var item = new Item { LinkshellId = Ls, ItemName = "Ridill" };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        var writer = NewWriter(db);
        var sales = new ItemSaleRecorder(db, writer);
        await sales.RecordSaleAsync(item, 1_000, Officer, null, CancellationToken.None);

        var saleEntry = await db.JournalEntries.SingleAsync(e => e.Id == item.JournalEntryId);
        await writer.ReverseAsync(saleEntry, "Reversed from the list.", Officer, CancellationToken.None);
        await db.SaveChangesAsync();

        await sales.ReverseSaleAsync(item, Officer, CancellationToken.None);

        Assert.False(item.IsSold);
        Assert.Null(item.JournalEntryId);
        Assert.Equal(0, await writer.GetCashOnHandAsync(Ls, CancellationToken.None));
        Assert.Equal(1, await db.JournalEntries.CountAsync(e => e.Kind == JournalEntryKinds.Reversal));
    }

    // A second "undo sale" (a double click, a stale page) on an item that is already unsold. The
    // item no longer links its entry, and the fallback lookup used to pick the sale's own REVERSAL
    // (same source item, confirmed, never reversed itself) and reverse that, putting the gil back.
    [Fact]
    public async Task UnsellTwice_TakesTheGilOffOnce()
    {
        await using var db = await SeededAsync();
        var item = new Item { LinkshellId = Ls, ItemName = "Ridill" };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        var writer = NewWriter(db);
        var sales = new ItemSaleRecorder(db, writer);
        await sales.RecordSaleAsync(item, 1_000, Officer, null, CancellationToken.None);

        await sales.ReverseSaleAsync(item, Officer, CancellationToken.None);
        await sales.ReverseSaleAsync(item, Officer, CancellationToken.None);

        Assert.False(item.IsSold);
        Assert.Equal(0, await writer.GetCashOnHandAsync(Ls, CancellationToken.None));
        Assert.Equal(1, await db.JournalEntries.CountAsync(e => e.Kind == JournalEntryKinds.Reversal));
    }

    // An item sold before Items.JournalEntryId existed is found by the fallback lookup. A second
    // undo must not pick the first undo's reversal (it names the same item as its source).
    [Fact]
    public async Task UnsellTwice_WithoutAStoredLink_TakesTheGilOffOnce()
    {
        await using var db = await SeededAsync();
        var item = new Item { LinkshellId = Ls, ItemName = "Ridill" };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        var writer = NewWriter(db);
        var sales = new ItemSaleRecorder(db, writer);
        await sales.RecordSaleAsync(item, 1_000, Officer, null, CancellationToken.None);
        item.JournalEntryId = null;
        await db.SaveChangesAsync();

        await sales.ReverseSaleAsync(item, Officer, CancellationToken.None);
        // A stale page still showing it sold.
        item.IsSold = true;
        await sales.ReverseSaleAsync(item, Officer, CancellationToken.None);

        Assert.Equal(0, await writer.GetCashOnHandAsync(Ls, CancellationToken.None));
        Assert.Equal(1, await db.JournalEntries.CountAsync(e => e.Kind == JournalEntryKinds.Reversal));
    }

    // The sale price was fixed from the treasury list (a Reversal of the original plus a Correction
    // with the right amount), then the sale is undone. The gil still on the books is the
    // Correction's, so that is what the undo reverses. Skipping it left the corrected amount on the
    // books for an item that is no longer sold.
    [Fact]
    public async Task UnsellAfterAFix_ReversesTheCorrectedEntry()
    {
        await using var db = await SeededAsync();
        var item = new Item { LinkshellId = Ls, ItemName = "Ridill" };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        var writer = NewWriter(db);
        var sales = new ItemSaleRecorder(db, writer);
        await sales.RecordSaleAsync(item, 1_000, Officer, null, CancellationToken.None);

        var saleEntry = await db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Id == item.JournalEntryId);
        var fixedPrice = new TreasuryEntryRequest(
            TreasuryTransactionKinds.SoldAnItem, 900, saleEntry.TransactionDate,
            Source: saleEntry.Source, SourceItemId: saleEntry.SourceItemId);
        await writer.CorrectAsync(saleEntry, fixedPrice, "Wrong price.", Officer, CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Equal(900, await writer.GetCashOnHandAsync(Ls, CancellationToken.None));

        await sales.ReverseSaleAsync(item, Officer, CancellationToken.None);

        Assert.False(item.IsSold);
        Assert.Equal(0, await writer.GetCashOnHandAsync(Ls, CancellationToken.None));
        Assert.Equal(2, await db.JournalEntries.CountAsync(e => e.Kind == JournalEntryKinds.Reversal));
    }
}
