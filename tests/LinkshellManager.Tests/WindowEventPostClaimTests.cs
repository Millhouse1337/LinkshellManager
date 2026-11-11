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

// P2.4: Post claims the card and pays it in one step only one request can win. These run on the
// in-memory provider (no transactions, one request at a time); the real race, two Posts at the same
// moment against Postgres, is in LinkshellManager.PostgresTests/PostClaimTests.cs.
public class WindowEventPostClaimTests
{
    private const int LinkshellId = 41;
    private const int CardId = 1;
    private const string AliceId = "user-alice";
    private static readonly DateTime Now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
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

    // One unposted Kings card with Alice on it, priced at 10.
    private static async Task<string> SeedAsync()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = NewDb(name);
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.Users.Add(new AppUser { Id = AliceId, UserName = "alice", CharacterName = "Alice" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = LinkshellId, AppUserId = AliceId, CharacterName = "Alice", LinkshellDkp = 0,
        });
        db.WindowEvents.Add(new WindowEvent
        {
            Id = CardId,
            LinkshellId = LinkshellId,
            Name = "Kings",
            Status = WindowEventStatuses.Closed,
            CreatedAtUtc = Now,
            FirstCapturedAtUtc = Now,
            LastCapturedAtUtc = Now,
            EntryType = WindowEventEntryTypes.KingsCamp,
            DkpAmount = 10d,
        });
        db.AttendanceSnapshots.Add(new AttendanceSnapshot
        {
            Id = 1,
            LinkshellId = LinkshellId,
            WindowEventId = CardId,
            CapturedAtUtc = Now,
            CreatedAtUtc = Now,
            SnapshotStatus = AttendanceSnapshotStatuses.Active,
            EntryCount = 1,
        });
        db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry
        {
            Id = 1, SnapshotId = 1, CharacterName = "Alice",
        });
        await db.SaveChangesAsync();
        return name;
    }

    [Fact]
    public async Task Post_SavesTheDraftMarksThePostAndPays()
    {
        var name = await SeedAsync();
        var postedAt = Now.AddHours(1);

        await using (var db = NewDb(name))
        {
            var card = await db.WindowEvents.SingleAsync(w => w.Id == CardId);
            card.DkpAmount = 12d; // the officer's draft, staged by the controller
            var paid = await NewLedgerService(db).PostAndPayAsync(card, postedAt, CancellationToken.None);
            Assert.Equal(1, paid);
        }

        await using (var db = NewDb(name))
        {
            var card = await db.WindowEvents.AsNoTracking().SingleAsync(w => w.Id == CardId);
            Assert.Equal(postedAt, card.PostedToSheetAt);
            Assert.Equal(12d, card.DkpAmount);
            var row = await db.DkpLedgerEntries.AsNoTracking().SingleAsync();
            Assert.Equal("SnapshotEarned", row.EntryType);
            Assert.Equal(12d, row.Amount);
            Assert.Equal(12d, (await db.AppUserLinkshells.AsNoTracking().SingleAsync()).LinkshellDkp);
        }
    }

    // Officer B loaded the card while it was still unposted (so the controller's early check let
    // B through), then officer A's Post landed first. B is turned away: nobody is paid twice, and
    // B's draft does not overwrite what A posted.
    [Fact]
    public async Task SecondPost_OfACardLoadedBeforeTheFirstLanded_IsTurnedAway()
    {
        var name = await SeedAsync();

        await using var dbB = NewDb(name);
        var cardSeenByB = await dbB.WindowEvents.SingleAsync(w => w.Id == CardId);
        Assert.Null(cardSeenByB.PostedToSheetAt);

        await using (var dbA = NewDb(name))
        {
            var cardA = await dbA.WindowEvents.SingleAsync(w => w.Id == CardId);
            Assert.Equal(1, await NewLedgerService(dbA).PostAndPayAsync(cardA, Now.AddHours(1), CancellationToken.None));
        }

        cardSeenByB.DkpAmount = 50d;
        var paidByB = await NewLedgerService(dbB).PostAndPayAsync(cardSeenByB, Now.AddHours(2), CancellationToken.None);
        Assert.Null(paidByB);

        await using (var db = NewDb(name))
        {
            var card = await db.WindowEvents.AsNoTracking().SingleAsync(w => w.Id == CardId);
            Assert.Equal(Now.AddHours(1), card.PostedToSheetAt);
            Assert.Equal(10d, card.DkpAmount);
            Assert.Equal(10d, (await db.DkpLedgerEntries.AsNoTracking().SingleAsync()).Amount);
            Assert.Equal(10d, (await db.AppUserLinkshells.AsNoTracking().SingleAsync()).LinkshellDkp);
        }
    }
}
