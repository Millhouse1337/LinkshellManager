using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// INV-3: a debit never takes a pool below zero unless the caller explicitly opts out.
//
// This used to be enforced only by LootDkpGuard, which was wired into the two EVENT-loot call
// sites and nothing else — so ToD loot (nine call sites) minted negative balances for months, and
// the DKP import wrote whatever the spreadsheet said straight over the top. Both holes are closed
// here: the floor lives in DkpLedgerWriter (every DKP move funnels through it) and the import
// clamps.
//
// The Allow cases matter as much as the Block ones: an officer audit exists to restate a balance
// past zero, and auction/event close settle a whole batch that was already checked upstream.
// Blocking those would be a worse bug than the one being fixed.
public class DkpOverdraftTests
{
    private const int Ls = 1;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // DkpImportService.CommitAsync opens a transaction; the in-memory provider has none.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static DkpLedgerWriter NewWriter(ApplicationDbContext db) =>
        new(db, new DkpPoolResolver(db, new DkpPoolProvisioner(db)), NullLogger<DkpLedgerWriter>.Instance);

    private static AppUserLinkshell Seed(ApplicationDbContext db, double balance)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "u1", UserName = "alice" });
        var member = new AppUserLinkshell
        {
            Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Alice", LinkshellDkp = balance
        };
        db.AppUserLinkshells.Add(member);
        db.SaveChanges();
        return member;
    }

    private static Task<DkpLedgerEntry?> SpendAsync(
        DkpLedgerWriter writer, AppUserLinkshell member, double cost,
        DkpOverdraft overdraft = DkpOverdraft.Block) =>
        writer.AppendAsync(
            member, "LootSpent", -cost, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DkpPoolRef.Derived("HNM"), new DkpEntryContext(), CancellationToken.None, overdraft);

    // ---- the floor -----------------------------------------------------------------

    [Fact]
    public async Task Debit_BeyondBalance_IsRejected_AndMovesNothing()
    {
        // The exact shape of the reported bug: 5 DKP in the wallet, a 10 DKP ToD item awarded.
        using var db = NewDb();
        var member = Seed(db, 5);
        var writer = NewWriter(db);

        await Assert.ThrowsAsync<DkpOverdraftException>(() => SpendAsync(writer, member, 10));

        // Rejected means rejected: no ledger row staged, no balance moved. A partial application
        // here would be worse than the overdraft, because the ledger would stop explaining it.
        Assert.Equal(5, member.LinkshellDkp);
        Assert.Empty(db.ChangeTracker.Entries<DkpLedgerEntry>());
    }

    [Fact]
    public async Task Debit_ExactlyToZero_IsAllowed()
    {
        // Spending your last DKP is legal. An off-by-one here would block the most common
        // "I saved up for exactly this item" case.
        using var db = NewDb();
        var member = Seed(db, 10);
        var writer = NewWriter(db);

        await SpendAsync(writer, member, 10);
        await db.SaveChangesAsync();

        Assert.Equal(0, member.LinkshellDkp);
        Assert.Single(db.DkpLedgerEntries);
    }

    [Fact]
    public async Task Debit_WithAllow_MayGoNegative()
    {
        // Officer intent (audits, adjustments) and already-checked batch settlement (auction and
        // event close). These MUST stay able to go negative — an audit is the tool you'd use to
        // fix an overdrawn member in the first place.
        using var db = NewDb();
        var member = Seed(db, 5);
        var writer = NewWriter(db);

        await SpendAsync(writer, member, 10, DkpOverdraft.Allow);
        await db.SaveChangesAsync();

        Assert.Equal(-5, member.LinkshellDkp);
    }

    [Fact]
    public async Task Credit_IsNeverBlocked_EvenFromANegativeBalance()
    {
        // Refunds and event earnings have to keep working for members who are ALREADY negative —
        // otherwise the existing bad balances could never be earned back out of.
        using var db = NewDb();
        var member = Seed(db, -76.25);
        var writer = NewWriter(db);

        await writer.AppendAsync(
            member, "EventEarned", 20, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DkpPoolRef.Derived("HNM"), new DkpEntryContext(), CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(-56.25, member.LinkshellDkp);
    }

    [Fact]
    public async Task TwoDebitsInOneRequest_CannotBothPass()
    {
        // Two items won off the same ToD, charged in one batch. The second read has to see the
        // first one's staged deduction or each passes on its own and together they overdraw —
        // which is exactly how a "guarded" path still goes negative.
        using var db = NewDb();
        var member = Seed(db, 10);
        var writer = NewWriter(db);

        await SpendAsync(writer, member, 6);
        await Assert.ThrowsAsync<DkpOverdraftException>(() => SpendAsync(writer, member, 6));

        Assert.Equal(4, member.LinkshellDkp);
    }

    // ---- the import ----------------------------------------------------------------

    // ManualMemberService is only reached for CREATE rows (it needs a UserManager, which is not
    // worth standing up here). Both tests below use an UPDATE row, which never touches it.
    private static DkpImportService NewImport(ApplicationDbContext db) =>
        new(db, null!, new DkpPoolResolver(db, new DkpPoolProvisioner(db)));

    [Fact]
    public async Task ImportPreview_NegativeSheetValue_ShowsZero_AndFlagsTheClamp()
    {
        // The officer has to see the clamp BEFORE committing — silently importing 0 where the
        // sheet said -40 is how you get a support question three weeks later.
        using var db = NewDb();
        Seed(db, 10);
        var rows = new[] { new DkpImportRow("Alice", null, null, Current: -40, Total: null, Spent: null) };

        var preview = await NewImport(db).BuildPreviewAsync(Ls, rows, "sheet.xlsx", CancellationToken.None);

        var row = Assert.Single(preview.Rows);
        Assert.Equal(0, row.NewCurrent);
        Assert.Equal(1, preview.ClampedCount);
        Assert.Contains("-40", row.Note);
    }

    [Fact]
    public async Task ImportCommit_NegativeSheetValue_LandsAtZero_WithAMatchingLedgerRow()
    {
        // The import is the one DKP write that does NOT debit through DkpLedgerWriter — it SETS a
        // balance — so INV-3 can't catch it and the clamp has to live in the import itself.
        using var db = NewDb();
        var member = Seed(db, 10);
        var rows = new[] { new DkpImportRow("Alice", null, null, Current: -40, Total: null, Spent: null) };

        await NewImport(db).CommitAsync(Ls, rows, "sheet.xlsx", CancellationToken.None);

        Assert.Equal(0, member.LinkshellDkp);
        // INV-1 still holds: the reconciliation row carries the delta actually applied (10 -> 0),
        // not the delta the sheet asked for.
        var entry = Assert.Single(db.DkpLedgerEntries);
        Assert.Equal(-10, entry.Amount);
    }
}
