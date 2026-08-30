using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The transactions chips. One predicate per chip, shared by the web and the Activity, so what
// "Fixed" means cannot differ between the two surfaces.
//
// Fixed and Reversed are the pair worth pinning. Both are recorded by the same mechanism — a later
// entry pointing at an earlier one — and the only thing telling them apart is the KIND of the entry
// doing the pointing. Get that wrong and every corrected typo lands in Reversed, which is exactly
// what the two chips were separated to stop.
public class TreasuryEntryFilterTests
{
    private const int Linkshell = 4;

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // One entry, reduced to the four fields every chip here reads. The lines carry the gil-on-hand
    // movement the direction chips key on; everything else about an entry is irrelevant to filtering.
    private static JournalEntry Entry(
        int id, string kind, int? reverses = null, long cashDelta = 0, string? status = null)
    {
        var entry = new JournalEntry
        {
            Id = id,
            LinkshellId = Linkshell,
            Sequence = id,
            EntryNumber = id.ToString("D6"),
            Status = status ?? JournalEntryStatuses.Confirmed,
            Kind = kind,
            TransactionDate = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc),
            ReversesJournalEntryId = reverses,
        };
        if (cashDelta != 0)
        {
            entry.Lines.Add(new JournalEntryLine
            {
                LinkshellId = Linkshell,
                AccountNumber = TreasuryAccounts.GilOnHand,
                AccountName = "Gil on hand",
                Amount = cashDelta,
                TransactionDate = entry.TransactionDate,
                LineNumber = 1,
            });
            entry.Lines.Add(new JournalEntryLine
            {
                LinkshellId = Linkshell,
                AccountNumber = TreasuryAccounts.ItemSales,
                AccountName = "Item sales",
                Amount = -cashDelta,
                TransactionDate = entry.TransactionDate,
                LineNumber = 2,
            });
        }
        return entry;
    }

    // The shape a Fix leaves behind, and the shape a plain Reverse leaves behind, side by side:
    //
    //   #1 an ordinary sale, later FIXED    -> #2 Reversal of #1, #3 Correction of #1
    //   #4 an ordinary sale, later REVERSED -> #5 Reversal of #4
    //   #6 an ordinary sale, untouched
    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewInMemoryContext();
        db.JournalEntries.AddRange(
            Entry(1, JournalEntryKinds.Standard, cashDelta: 100_000),
            Entry(2, JournalEntryKinds.Reversal, reverses: 1, cashDelta: -100_000),
            Entry(3, JournalEntryKinds.Correction, reverses: 1, cashDelta: 150_000),
            Entry(4, JournalEntryKinds.Standard, cashDelta: 200_000),
            Entry(5, JournalEntryKinds.Reversal, reverses: 4, cashDelta: -200_000),
            Entry(6, JournalEntryKinds.Standard, cashDelta: 50_000));
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<int[]> FilteredAsync(ApplicationDbContext db, string? filter) =>
        await TreasuryEntryFilters
            .Apply(db.JournalEntries.AsNoTracking().Include(entry => entry.Lines), filter)
            .Select(entry => entry.Id)
            .OrderBy(id => id)
            .ToArrayAsync();

    // What it said and what it says now. The reversal recorded in between (#2) is left out: it is
    // the same figures with the sign flipped and tells the reader nothing.
    [Fact]
    public async Task Fixed_ShowsTheEntryAndItsReplacement()
    {
        using var db = await SeededAsync();

        Assert.Equal(new[] { 1, 3 }, await FilteredAsync(db, TreasuryEntryFilters.Fixed));
    }

    // THE point of separating the two chips. Before, #1 and #2 landed here as well, so a linkshell
    // that fixes a dozen typos could no longer find the one entry it actually called off.
    [Fact]
    public async Task Reversed_ShowsOnlyWhatWasCalledOff_NotWhatWasFixed()
    {
        using var db = await SeededAsync();

        Assert.Equal(new[] { 4, 5 }, await FilteredAsync(db, TreasuryEntryFilters.Reversed));
    }

    // A correction is not immune: fix an entry, then reverse the replacement, and the replacement
    // was genuinely called off. It belongs under Reversed even though it is a Correction — which is
    // why the "reversal half of a fix" test is guarded on the entry being a Reversal.
    [Fact]
    public async Task Reversed_IncludesACorrectionThatWasItselfReversed()
    {
        using var db = await SeededAsync();
        db.JournalEntries.Add(Entry(7, JournalEntryKinds.Reversal, reverses: 3, cashDelta: -150_000));
        await db.SaveChangesAsync();

        var reversed = await FilteredAsync(db, TreasuryEntryFilters.Reversed);
        Assert.Contains(3, reversed);
        Assert.Contains(7, reversed);
        // And the entry that was only ever fixed still does not appear.
        Assert.DoesNotContain(1, reversed);
    }

    // Direction is the sign of the gil-on-hand line, never a string match on a category — which is
    // why an entry that moves no gil on hand (an obligation) is under neither.
    [Fact]
    public async Task Direction_ReadsTheGilOnHandLine()
    {
        using var db = await SeededAsync();
        db.JournalEntries.Add(Entry(8, JournalEntryKinds.Standard));
        await db.SaveChangesAsync();

        Assert.Equal(new[] { 1, 3, 4, 6 }, await FilteredAsync(db, TreasuryEntryFilters.GilIn));
        Assert.Equal(new[] { 2, 5 }, await FilteredAsync(db, TreasuryEntryFilters.GilOut));
        Assert.DoesNotContain(8, await FilteredAsync(db, TreasuryEntryFilters.GilIn));
        Assert.DoesNotContain(8, await FilteredAsync(db, TreasuryEntryFilters.GilOut));
    }

    // An unknown chip value — a stale bookmark, a hand-typed query string — shows everything rather
    // than nothing. "drafts" is one of those now: it was a chip until the row was trimmed.
    // "uncategorized" is one of those now: with the income and expense buckets gone, every
    // hand-recorded Gil In and Gil Out lands in the catch-all on purpose, so a filter for "the ones
    // that landed in the catch-all" would just be "all of them".
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("drafts")]
    [InlineData("uncategorized")]
    [InlineData("NotAFilter")]
    public async Task UnknownFilters_FallBackToEverything(string? filter)
    {
        using var db = await SeededAsync();

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, await FilteredAsync(db, filter));
    }
}
