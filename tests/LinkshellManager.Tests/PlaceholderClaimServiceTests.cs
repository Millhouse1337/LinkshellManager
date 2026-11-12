using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LinkshellManager.Tests;

// Claiming an imported placeholder (PlaceholderClaimService). The claim used to COPY the imported
// figures over the real membership, so DKP the member earned on their own account before claiming
// simply vanished. Now the two balances (and lifetime totals) are added, the ledger still adds up to
// the balance, and one 0-DKP "PlaceholderMerge" row explains the jump in the DKP history.
//
// The "nothing points at the deleted placeholder" check runs against real Postgres
// (PostgresTests/PlaceholderClaimMergeTests): the in-memory database has no foreign keys.
public class PlaceholderClaimServiceTests
{
    private const int Ls = 1;
    private const string Real = "real";
    private const string Ph = "ph";
    private static readonly DateTime When = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewDb()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // The claim opens a transaction; the in-memory provider has none.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS" });
        db.Users.Add(new AppUser { Id = Real, UserName = "real", CharacterName = "Bob" });
        db.Users.Add(new AppUser { Id = Ph, UserName = "ph", CharacterName = "Bobalt", AltCharacterName1 = "Bobthree", IsPlaceholder = true });
        db.SaveChanges();
        return db;
    }

    private static PlaceholderClaimService NewService(ApplicationDbContext db) => new(db, new UserMergeService(db));

    private static AppUserLinkshell Member(ApplicationDbContext db, int id, string appUserId, string name, double dkp)
    {
        var member = new AppUserLinkshell { Id = id, LinkshellId = Ls, AppUserId = appUserId, CharacterName = name, LinkshellDkp = dkp };
        db.AppUserLinkshells.Add(member);
        db.SaveChanges();
        return member;
    }

    private static void Ledger(ApplicationDbContext db, int id, string appUserId, string type, double amount)
    {
        db.DkpLedgerEntries.Add(new DkpLedgerEntry { Id = id, LinkshellId = Ls, AppUserId = appUserId, EntryType = type, Amount = amount, OccurredAt = When });
        db.SaveChanges();
    }

    [Fact]
    public async Task NotYetAMember_TakesOverTheRosterSpot_AndThePlaceholderIsDeleted()
    {
        using var db = NewDb();
        var spot = Member(db, 10, Ph, "Bobalt", 100);
        spot.DiscordUserId = "999";
        Ledger(db, 1, Ph, "SheetImport", 100);
        db.SaveChanges();

        var result = await NewService(db).ClaimPlaceholderAsync(Ph, Real, Ls, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var member = Assert.Single(db.AppUserLinkshells);
        Assert.Equal(10, member.Id);
        Assert.Equal(Real, member.AppUserId);
        Assert.Equal(100d, member.LinkshellDkp);
        Assert.Null(member.DiscordUserId);
        Assert.Null(db.Users.Find(Ph));
        // The real account had no alt; it takes the placeholder's.
        Assert.Equal("Bobthree", db.Users.Find(Real)!.AltCharacterName1);

        var rows = db.DkpLedgerEntries.ToList();
        Assert.All(rows, r => Assert.Equal(Real, r.AppUserId));
        Assert.Equal(100d, rows.Sum(r => r.Amount));
        var note = Assert.Single(rows, r => r.EntryType == PlaceholderClaimService.MergeEntryType);
        Assert.Equal(0d, note.Amount);
    }

    [Fact]
    public async Task NotYetAMember_RowsLeftFromAnEarlierStay_DoNotCountTowardAPool()
    {
        // Bob was on this roster after the import, earned 50 HNM-pool DKP (row 5), then his own
        // entry was removed. That 50 is in no balance. Taking over the imported spot must not make
        // it spendable HNM DKP again (the pool check would let him spend 50 he does not have).
        using var db = NewDb();
        Member(db, 11, Ph, "Bobalt", 100);
        Ledger(db, 1, Ph, "SheetImport", 100);
        db.DkpLedgerEntries.Add(new DkpLedgerEntry { Id = 5, LinkshellId = Ls, AppUserId = Real, EntryType = "SnapshotEarned", Amount = 50, DkpPoolId = 7, OccurredAt = When });
        db.SaveChanges();

        var result = await NewService(db).ClaimPlaceholderAsync(Ph, Real, Ls, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var member = Assert.Single(db.AppUserLinkshells);
        Assert.Equal(100d, member.LinkshellDkp);
        Assert.Equal(5, member.DkpPoolLedgerFromId);
        var rows = db.DkpLedgerEntries.Select(e => new LedgerPoolRow(e.Id, e.DkpPoolId, e.Amount)).ToList();
        var pools = DkpPoolBalanceService.Project(member.LinkshellDkp ?? 0, rows, member.DkpPoolLedgerFromId, 1, new[] { 1, 7 });
        Assert.Equal(0d, pools[7]);
        Assert.Equal(100d, pools[1]);
    }

    [Fact]
    public async Task AlreadyAMember_AddsBothBalances_AndBothLifetimeTotals()
    {
        // Bob earned 30 on his own account; the import gave Bobalt 100. Claiming must leave 130,
        // not 100 (the old overwrite).
        using var db = NewDb();
        var real = Member(db, 10, Real, "Bob", 30);
        real.SeededDkpEarned = 10;
        real.SeededDkpSpent = 5;
        var ph = Member(db, 11, Ph, "Bobalt", 100);
        ph.SeededDkpEarned = 200;
        ph.SeededDkpSpent = 50;
        db.SaveChanges();
        Ledger(db, 1, Real, "EventEarned", 30);
        Ledger(db, 2, Ph, "SheetImport", 100);

        var result = await NewService(db).ClaimPlaceholderAsync(Ph, Real, Ls, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var member = Assert.Single(db.AppUserLinkshells);
        Assert.Equal(10, member.Id);
        Assert.Equal(130d, member.LinkshellDkp);
        Assert.Equal(210d, member.SeededDkpEarned);
        Assert.Equal(55d, member.SeededDkpSpent);
        // The ledger still explains the balance, with no DKP written twice.
        Assert.Equal(130d, db.DkpLedgerEntries.Where(e => e.AppUserId == Real).Sum(e => e.Amount));
        Assert.Contains(db.DkpLedgerEntries, e => e.EntryType == PlaceholderClaimService.MergeEntryType && e.Amount == 0);
        Assert.Null(db.Users.Find(Ph));
    }

    [Fact]
    public async Task AlreadyAMember_RowsInsideTheRealSeed_AreNotCountedTwice()
    {
        // Bob's account was seeded by an import at ledger row 1 (his 20 earned is inside the 500).
        // Bobalt's rows start counting from 0. The merged row counts from 0 too, so Bob's row 1
        // comes off the seed: lifetime stays 500 + 10 = 510, not 530.
        using var db = NewDb();
        var real = Member(db, 10, Real, "Bob", 20);
        real.SeededDkpEarned = 500;
        real.DkpSeedLedgerId = 1;
        Member(db, 11, Ph, "Bobalt", 10);
        db.SaveChanges();
        Ledger(db, 1, Real, "EventEarned", 20);
        Ledger(db, 2, Ph, "EventEarned", 10);

        var result = await NewService(db).ClaimPlaceholderAsync(Ph, Real, Ls, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var member = Assert.Single(db.AppUserLinkshells);
        Assert.Equal(0, member.DkpSeedLedgerId);
        Assert.Equal(480d, member.SeededDkpEarned);
        var counted = db.DkpLedgerEntries
            .Where(e => e.AppUserId == Real && e.Id > member.DkpSeedLedgerId && e.Amount > 0)
            .Sum(e => e.Amount);
        Assert.Equal(510d, member.SeededDkpEarned + counted);
    }

    [Fact]
    public async Task AlreadyAMember_PoolSplitStartsAtTheEarlierJoin_UnlessTheLaterHasOldRows()
    {
        using var db = NewDb();
        var real = Member(db, 10, Real, "Bob", 0);
        real.DkpPoolLedgerFromId = 5;
        var ph = Member(db, 11, Ph, "Bobalt", 0);
        ph.DkpPoolLedgerFromId = 2;
        db.SaveChanges();

        var result = await NewService(db).ClaimPlaceholderAsync(Ph, Real, Ls, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, Assert.Single(db.AppUserLinkshells).DkpPoolLedgerFromId);
    }

    [Fact]
    public async Task AlreadyAMember_WithLeftoverRowsFromAnEarlierStay_KeepsTheLaterPoolStart()
    {
        // Bob has row 3 from an earlier time in this linkshell (before he rejoined at 5). It is not
        // part of his balance, so it must not start counting toward a pool.
        using var db = NewDb();
        var real = Member(db, 10, Real, "Bob", 0);
        real.DkpPoolLedgerFromId = 5;
        var ph = Member(db, 11, Ph, "Bobalt", 0);
        ph.DkpPoolLedgerFromId = 2;
        db.SaveChanges();
        Ledger(db, 3, Real, "EventEarned", 0);

        var result = await NewService(db).ClaimPlaceholderAsync(Ph, Real, Ls, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(5, Assert.Single(db.AppUserLinkshells).DkpPoolLedgerFromId);
    }

    [Fact]
    public async Task AlreadyAMember_ChartRowsFollowTheFoldedMembership()
    {
        using var db = NewDb();
        Member(db, 10, Real, "Bob", 0);
        Member(db, 11, Ph, "Bobalt", 0);
        db.ChartMemberKeyItems.AddRange(
            new ChartMemberKeyItem { Id = 1, LinkshellId = Ls, Board = "Sky", KeyItemName = "A", MembershipId = 10, CharacterName = "Bob" },
            new ChartMemberKeyItem { Id = 2, LinkshellId = Ls, Board = "Sky", KeyItemName = "A", MembershipId = 11, CharacterName = "Bobalt" },
            new ChartMemberKeyItem { Id = 3, LinkshellId = Ls, Board = "Sky", KeyItemName = "B", MembershipId = 11, CharacterName = "Bobalt" });
        db.ChartWishlistRequests.Add(new ChartWishlistRequest { Id = 1, LinkshellId = Ls, RequestedByMembershipId = 11, RequestedByAppUserId = Ph });
        db.SaveChanges();

        var result = await NewService(db).ClaimPlaceholderAsync(Ph, Real, Ls, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { 1, 3 }, db.ChartMemberKeyItems.OrderBy(k => k.Id).Select(k => k.Id));
        Assert.All(db.ChartMemberKeyItems, k => Assert.Equal(10, k.MembershipId));
        var request = Assert.Single(db.ChartWishlistRequests);
        Assert.Equal(10, request.RequestedByMembershipId);
        Assert.Equal(Real, request.RequestedByAppUserId);
    }

    [Fact]
    public async Task APlaceholderOnTwoLinkshells_CannotBeClaimedFromOne_AndNothingChanges()
    {
        using var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = 2, LinkshellName = "Other" });
        Member(db, 11, Ph, "Bobalt", 40);
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 12, LinkshellId = 2, AppUserId = Ph, CharacterName = "Bobalt", LinkshellDkp = 7 });
        db.SaveChanges();

        var result = await NewService(db).ClaimPlaceholderAsync(Ph, Real, Ls, CancellationToken.None);

        Assert.False(result.Success);
        Assert.All(db.AppUserLinkshells, m => Assert.Equal(Ph, m.AppUserId));
        Assert.NotNull(db.Users.Find(Ph));
    }

    [Fact]
    public async Task ARealAccount_CannotBeClaimedAsAPlaceholder()
    {
        using var db = NewDb();
        Member(db, 10, Real, "Bob", 30);

        var result = await NewService(db).ClaimPlaceholderAsync(Real, Ph, Ls, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(Real, Assert.Single(db.AppUserLinkshells).AppUserId);
    }
}
