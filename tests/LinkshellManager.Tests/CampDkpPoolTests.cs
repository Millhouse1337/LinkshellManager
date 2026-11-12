using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// A linkshell that gives HNM its own DKP pool: an HNM camp's loot was charged from that pool, but
// the camp's DKP was filed under the card's entry type ("Kings Camp"), which nobody can map, so it
// landed in the default pool. Both pool balances were wrong. Camp DKP now follows the camp's own
// event type; a "/lsm now" card (no camp) still follows its entry type.
public class CampDkpPoolTests
{
    private const int LinkshellId = 31;
    private const int CardId = 1;
    private const int MainPool = 100;
    private const int HnmPool = 101;
    private const string BobId = "user-bob";
    private static readonly DateTime CampStart = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static WindowEventDkpLedgerService NewLedgerService(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new WindowEventDkpLedgerService(
            db,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            NullLogger<WindowEventDkpLedgerService>.Instance);
    }

    // Bob captured once on a posted 5-DKP card. `campEventType` null is a "/lsm now" card.
    private static async Task<ApplicationDbContext> SeededAsync(string? campEventType, double bobDkp = 0)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.Users.Add(new AppUser { Id = BobId, UserName = "bob", CharacterName = "Bob" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 2, LinkshellId = LinkshellId, AppUserId = BobId, CharacterName = "Bob", LinkshellDkp = bobDkp,
        });
        db.DkpPools.Add(new DkpPool { Id = MainPool, LinkshellId = LinkshellId, Name = "Main", IsDefault = true });
        db.DkpPools.Add(new DkpPool { Id = HnmPool, LinkshellId = LinkshellId, Name = "HNM", SortOrder = 1 });
        db.DkpPoolEventTypes.Add(new DkpPoolEventType
        {
            LinkshellId = LinkshellId, DkpPoolId = HnmPool, EventType = "HNM",
            NormalizedEventType = DkpPoolEventType.Normalize("HNM"),
        });
        db.WindowEvents.Add(new WindowEvent
        {
            Id = CardId,
            LinkshellId = LinkshellId,
            Name = "Fafnir",
            Status = WindowEventStatuses.Open,
            CreatedAtUtc = CampStart,
            FirstCapturedAtUtc = CampStart,
            LastCapturedAtUtc = CampStart.AddHours(1),
            EntryType = WindowEventEntryTypes.KingsCamp,
            DkpAmount = 5,
            PostedToSheetAt = CampStart.AddHours(2),
            CampEventType = campEventType,
        });
        db.AttendanceSnapshots.Add(new AttendanceSnapshot
        {
            Id = 1,
            LinkshellId = LinkshellId,
            WindowEventId = CardId,
            CapturedAtUtc = CampStart.AddMinutes(10),
            SnapshotStatus = AttendanceSnapshotStatuses.Active,
            EntryCount = 1,
        });
        db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry { Id = 1, SnapshotId = 1, CharacterName = "Bob" });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task ACampCard_PaysIntoTheCampsOwnPool()
    {
        await using var db = await SeededAsync(campEventType: "HNM");

        await NewLedgerService(db).EnsurePostedWindowEventLedgerEntriesAsync(CardId, CancellationToken.None);

        var earned = await db.DkpLedgerEntries.SingleAsync(e => e.AppUserId == BobId);
        Assert.Equal(HnmPool, earned.DkpPoolId);
        Assert.Equal("HNM", earned.EventType);
    }

    [Fact]
    public async Task ALsmNowCard_IsStillFiledUnderItsEntryType()
    {
        await using var db = await SeededAsync(campEventType: null);

        await NewLedgerService(db).EnsurePostedWindowEventLedgerEntriesAsync(CardId, CancellationToken.None);

        var earned = await db.DkpLedgerEntries.SingleAsync(e => e.AppUserId == BobId);
        Assert.Equal(MainPool, earned.DkpPoolId);
        Assert.Equal(WindowEventEntryTypes.KingsCamp, earned.EventType);
    }

    // A camp posted before this fix sits in the default pool under "Kings Camp". Update moves it
    // (and an audit of it) to the camp's pool; the amounts and the member's total do not change.
    [Fact]
    public async Task Update_MovesAnOldCampRowIntoTheCampsPool()
    {
        await using var db = await SeededAsync(campEventType: "HNM", bobDkp: 4);
        var paid = new DkpLedgerEntry
        {
            LinkshellId = LinkshellId, AppUserId = BobId, EntryType = "SnapshotEarned", Amount = 5,
            SourceWindowEventId = CardId, EventType = WindowEventEntryTypes.KingsCamp, DkpPoolId = MainPool,
            OccurredAt = CampStart,
        };
        db.DkpLedgerEntries.Add(paid);
        await db.SaveChangesAsync();
        db.DkpLedgerEntries.Add(new DkpLedgerEntry
        {
            LinkshellId = LinkshellId, AppUserId = BobId, EntryType = "AuditAdjustment", Amount = -1,
            SourceWindowEventId = CardId, EventType = WindowEventEntryTypes.KingsCamp, DkpPoolId = MainPool,
            AuditRelatedLedgerEntryId = paid.Id, OccurredAt = CampStart,
        });
        await db.SaveChangesAsync();

        await NewLedgerService(db).ReconcilePostedWindowEventLedgerAsync(CardId, CancellationToken.None);

        var rows = await db.DkpLedgerEntries.AsNoTracking().ToListAsync();
        Assert.All(rows, row => Assert.Equal(HnmPool, row.DkpPoolId));
        Assert.All(rows, row => Assert.Equal("HNM", row.EventType));
        Assert.Equal(4, rows.Sum(row => row.Amount));
        Assert.Equal(4, (await db.AppUserLinkshells.SingleAsync()).LinkshellDkp);
    }
}
