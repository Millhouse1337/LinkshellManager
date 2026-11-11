using System;
using System.Collections.Generic;
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

// "Pay newly registered" on a posted card.
//
// Post pays only who is registered at that moment; the page-load sweep that used to pay latecomers
// (and could pay a camp twice) is gone. So someone captured on a card who registers afterwards is
// paid by an officer pressing the button: once, at the card's rate, and never anyone already paid.
// The attendance payload finds those cards with CountUnpaidAccountsAsync, which has to agree with
// what the button would pay.
public class PayNewlyRegisteredTests
{
    private const int LinkshellId = 41;
    private const int CardId = 1;
    private const int HistoryId = 7;
    private const string AliceId = "user-alice";
    private const string CarolId = "user-carol";
    private static readonly DateTime CapturedAt = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

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

    // An unposted card priced at 10 with Alice (a member) and Carol (not registered yet) on it.
    // `camp`: the card is an HNM camp archived as a Past Event that already lists Alice.
    private static async Task<ApplicationDbContext> SeededAsync(bool camp = false)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.Users.Add(new AppUser { Id = AliceId, UserName = "alice", CharacterName = "Alice" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = LinkshellId, AppUserId = AliceId, CharacterName = "Alice", LinkshellDkp = 0,
        });

        if (camp)
        {
            db.EventHistories.Add(new EventHistory
            {
                Id = HistoryId,
                LinkshellId = LinkshellId,
                EventName = "Fafnir",
                EventType = "HNM",
                StartTime = CapturedAt,
                EndTime = CapturedAt.AddHours(1),
                TimeStamp = CapturedAt.AddHours(1),
            });
            db.AppUserEventHistories.Add(new AppUserEventHistory
            {
                EventHistoryId = HistoryId, AppUserId = AliceId, CharacterName = "Alice", EventDkp = 10d,
            });
        }

        db.WindowEvents.Add(new WindowEvent
        {
            Id = CardId,
            LinkshellId = LinkshellId,
            Name = "Fafnir",
            Status = WindowEventStatuses.Closed,
            CreatedAtUtc = CapturedAt,
            FirstCapturedAtUtc = CapturedAt,
            LastCapturedAtUtc = CapturedAt,
            EntryType = WindowEventEntryTypes.KingsCamp,
            DkpAmount = 10d,
            CampStartedAtUtc = camp ? CapturedAt : null,
            CampEndedAtUtc = camp ? CapturedAt.AddHours(1) : null,
            CampEventType = camp ? "HNM" : null,
            CampEventHistoryId = camp ? HistoryId : null,
        });
        db.AttendanceSnapshots.Add(new AttendanceSnapshot
        {
            Id = 1,
            LinkshellId = LinkshellId,
            WindowEventId = CardId,
            CapturedAtUtc = CapturedAt,
            CreatedAtUtc = CapturedAt,
            SnapshotStatus = AttendanceSnapshotStatuses.Active,
            EntryCount = 2,
        });
        db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry { Id = 1, SnapshotId = 1, CharacterName = "Alice" });
        db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry { Id = 2, SnapshotId = 1, CharacterName = "Carol" });

        await db.SaveChangesAsync();
        return db;
    }

    private static async Task PostAsync(ApplicationDbContext db, DateTime postedAtUtc)
    {
        var card = await db.WindowEvents.SingleAsync(w => w.Id == CardId);
        Assert.NotNull(await NewLedgerService(db).PostAndPayAsync(card, postedAtUtc, CancellationToken.None));
    }

    // Carol accepts the linkshell invite (or signs up on a board) after the card was posted.
    private static async Task RegisterCarolAsync(ApplicationDbContext db)
    {
        db.Users.Add(new AppUser { Id = CarolId, UserName = "carol", CharacterName = "Carol" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 2, LinkshellId = LinkshellId, AppUserId = CarolId, CharacterName = "Carol", LinkshellDkp = 0,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<double> BalanceAsync(ApplicationDbContext db, string appUserId) =>
        (await db.AppUserLinkshells.AsNoTracking().SingleAsync(l => l.AppUserId == appUserId)).LinkshellDkp ?? 0d;

    private static Task<List<DkpLedgerEntry>> EarnedAsync(ApplicationDbContext db) =>
        db.DkpLedgerEntries.AsNoTracking().Where(e => e.EntryType == "SnapshotEarned").ToListAsync();

    private static Task<Dictionary<int, int>> OwingAsync(ApplicationDbContext db, DateTime? since = null) =>
        NewLedgerService(db).CountUnpaidAccountsAsync(
            LinkshellId, since ?? CapturedAt.AddDays(-1), CancellationToken.None);

    [Fact]
    public async Task RegisteredAfterPost_IsPaidOnce_AndNobodyElseAgain()
    {
        await using var db = await SeededAsync();
        await PostAsync(db, CapturedAt.AddHours(2));
        Assert.Equal(10d, await BalanceAsync(db, AliceId));

        await RegisterCarolAsync(db);

        Assert.Equal(1, await NewLedgerService(db).PayNewlyRegisteredAsync(CardId, CancellationToken.None));
        Assert.Equal(10d, await BalanceAsync(db, CarolId));
        Assert.Equal(10d, await BalanceAsync(db, AliceId));

        // Pressed again (or by a second officer afterwards): nobody is owed, nothing moves.
        Assert.Equal(0, await NewLedgerService(db).PayNewlyRegisteredAsync(CardId, CancellationToken.None));
        Assert.Equal(10d, await BalanceAsync(db, CarolId));
        Assert.Equal(10d, await BalanceAsync(db, AliceId));

        var earned = await EarnedAsync(db);
        Assert.Single(earned, e => e.AppUserId == AliceId);
        Assert.Single(earned, e => e.AppUserId == CarolId && e.SourceWindowEventId == CardId && e.Amount == 10d);
    }

    [Fact]
    public async Task NobodyNewlyRegistered_PaysNothing()
    {
        await using var db = await SeededAsync();
        await PostAsync(db, CapturedAt.AddHours(2));

        // Carol is still not on the roster: there is no account to pay.
        Assert.Equal(0, await NewLedgerService(db).PayNewlyRegisteredAsync(CardId, CancellationToken.None));
        Assert.Single(await EarnedAsync(db));
        Assert.Equal(10d, await BalanceAsync(db, AliceId));
    }

    [Fact]
    public async Task UnpostedCard_IsTurnedAway_AndPaysNobody()
    {
        await using var db = await SeededAsync();
        await RegisterCarolAsync(db);

        Assert.Null(await NewLedgerService(db).PayNewlyRegisteredAsync(CardId, CancellationToken.None));
        Assert.Empty(await EarnedAsync(db));
        Assert.Null((await db.WindowEvents.AsNoTracking().SingleAsync()).PostedToSheetAt);
    }

    [Fact]
    public async Task UnknownCard_IsTurnedAway()
    {
        await using var db = await SeededAsync();
        Assert.Null(await NewLedgerService(db).PayNewlyRegisteredAsync(999, CancellationToken.None));
    }

    // An officer corrected the card after posting (Edit Posted) before Carol registered: she is paid
    // the card's amount as it stands now.
    [Fact]
    public async Task PaysAtTheCardsCurrentAmount()
    {
        await using var db = await SeededAsync();
        await PostAsync(db, CapturedAt.AddHours(2));
        var card = await db.WindowEvents.SingleAsync(w => w.Id == CardId);
        card.DkpAmount = 12d;
        await db.SaveChangesAsync();
        await RegisterCarolAsync(db);

        Assert.Equal(1, await NewLedgerService(db).PayNewlyRegisteredAsync(CardId, CancellationToken.None));
        Assert.Equal(12d, await BalanceAsync(db, CarolId));
    }

    // On a camp, the newcomer is also added to the camp's Past Event, once.
    [Fact]
    public async Task OnACamp_AddsTheNewcomerToThePastEventOnce()
    {
        await using var db = await SeededAsync(camp: true);
        await PostAsync(db, CapturedAt.AddHours(2));
        await RegisterCarolAsync(db);

        await NewLedgerService(db).PayNewlyRegisteredAsync(CardId, CancellationToken.None);
        await NewLedgerService(db).PayNewlyRegisteredAsync(CardId, CancellationToken.None);

        var archive = await db.AppUserEventHistories.AsNoTracking()
            .Where(r => r.EventHistoryId == HistoryId)
            .ToListAsync();
        Assert.Single(archive, r => r.AppUserId == AliceId);
        var carol = Assert.Single(archive, r => r.AppUserId == CarolId);
        Assert.Equal(10d, carol.EventDkp);
    }

    [Fact]
    public async Task Count_ListsTheCard_OnlyWhileSomeoneIsOwed()
    {
        await using var db = await SeededAsync();
        await PostAsync(db, CapturedAt.AddHours(2));

        // Carol not registered: nobody the card could pay is unpaid.
        Assert.Empty(await OwingAsync(db));

        await RegisterCarolAsync(db);
        var owing = await OwingAsync(db);
        Assert.Equal(1, Assert.Single(owing).Value);
        Assert.Equal(CardId, owing.Keys.Single());

        // What the count promised is what the button pays.
        Assert.Equal(owing[CardId], await NewLedgerService(db).PayNewlyRegisteredAsync(CardId, CancellationToken.None));
        Assert.Empty(await OwingAsync(db));
    }

    [Fact]
    public async Task Count_SkipsUnpostedCards_AndCardsPostedBeforeTheLookback()
    {
        await using var db = await SeededAsync();
        await RegisterCarolAsync(db);

        // Unposted: Post pays everyone, so nothing is "owed" yet.
        Assert.Empty(await OwingAsync(db));

        await PostAsync(db, CapturedAt.AddHours(2));
        // Carol was registered at Post, so she was paid with it.
        Assert.Empty(await OwingAsync(db));

        var carolPaid = await db.DkpLedgerEntries.SingleAsync(e => e.AppUserId == CarolId);
        db.DkpLedgerEntries.Remove(carolPaid);
        await db.SaveChangesAsync();
        Assert.Single(await OwingAsync(db));
        Assert.Empty(await OwingAsync(db, since: CapturedAt.AddDays(1)));
    }

    // Carol captured on her alt counts as one person owed, and any row from the card (an officer's
    // audit Add, say) counts as paid -- the payout's own rule.
    [Fact]
    public async Task Count_FoldsAltsAndHonoursAnyRowFromTheCard()
    {
        await using var db = await SeededAsync();
        db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry { Id = 3, SnapshotId = 1, CharacterName = "Carolalt" });
        await db.SaveChangesAsync();
        await PostAsync(db, CapturedAt.AddHours(2));

        db.Users.Add(new AppUser
        {
            Id = CarolId, UserName = "carol", CharacterName = "Carol", AltCharacterName1 = "Carolalt",
        });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 2, LinkshellId = LinkshellId, AppUserId = CarolId, CharacterName = "Carol", LinkshellDkp = 0,
        });
        await db.SaveChangesAsync();
        Assert.Equal(1, (await OwingAsync(db))[CardId]);

        db.DkpLedgerEntries.Add(new DkpLedgerEntry
        {
            LinkshellId = LinkshellId,
            AppUserId = CarolId,
            SourceWindowEventId = CardId,
            EntryType = "AuditAdjustment",
            Amount = 10d,
            OccurredAt = CapturedAt,
        });
        await db.SaveChangesAsync();
        Assert.Empty(await OwingAsync(db));
        Assert.Equal(0, await NewLedgerService(db).PayNewlyRegisteredAsync(CardId, CancellationToken.None));
    }

    // A rejected capture is not on the card's roster, so nobody in it is owed.
    [Fact]
    public async Task Count_IgnoresRejectedCaptures()
    {
        await using var db = await SeededAsync();
        await PostAsync(db, CapturedAt.AddHours(2));
        db.AttendanceSnapshots.Add(new AttendanceSnapshot
        {
            Id = 2,
            LinkshellId = LinkshellId,
            WindowEventId = CardId,
            CapturedAtUtc = CapturedAt,
            CreatedAtUtc = CapturedAt,
            SnapshotStatus = AttendanceSnapshotStatuses.Ignored,
            EntryCount = 1,
        });
        db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry { Id = 3, SnapshotId = 2, CharacterName = "Dave" });
        db.Users.Add(new AppUser { Id = "user-dave", UserName = "dave", CharacterName = "Dave" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 3, LinkshellId = LinkshellId, AppUserId = "user-dave", CharacterName = "Dave", LinkshellDkp = 0,
        });
        await db.SaveChangesAsync();

        Assert.Empty(await OwingAsync(db));
        Assert.Equal(0, await NewLedgerService(db).PayNewlyRegisteredAsync(CardId, CancellationToken.None));
    }
}
