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

// A posted card pays each PERSON once, and Update never re-prices an officer's correction.
//
// The card's roster is one line per CHARACTER, and members are often captured on an alt in one
// capture and on their main in another. Post used to write a ledger row per character name: a flat
// "/lsm now" card paid that member twice, and on a priced camp their pay was split over two rows
// that Past Events and Update each priced on their own (2.5 + 4 became 4 + 4 on Update).
//
// Update also rewrote EVERY row carrying the card's id, AuditAdjustment rows included: Bob paid
// 10 and audited down to 8 became Bob 20 the next time anyone pressed Update.
public class WindowEventOneCreditPerAccountTests
{
    private const int LinkshellId = 31;
    private const int CardId = 1;
    private const int HistoryId = 9;
    private const string AlphaId = "user-alpha";
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

    // Alpha (alt "Alphalt") and Bob. Each capture is (character, amount on that capture); a priced
    // card is a camp archived as a Past Event, a flat card is a plain "/lsm now" card.
    private static async Task<ApplicationDbContext> SeededAsync(
        bool priced, double dkpAmount, params (string Name, double? Amount)[][] captures)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.Users.Add(new AppUser
        {
            Id = AlphaId, UserName = "alpha", CharacterName = "Alpha", AltCharacterName1 = "Alphalt",
        });
        db.Users.Add(new AppUser { Id = BobId, UserName = "bob", CharacterName = "Bob" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = LinkshellId, AppUserId = AlphaId, CharacterName = "Alpha", LinkshellDkp = 0,
        });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 2, LinkshellId = LinkshellId, AppUserId = BobId, CharacterName = "Bob", LinkshellDkp = 0,
        });

        if (priced)
        {
            db.EventHistories.Add(new EventHistory
            {
                Id = HistoryId,
                LinkshellId = LinkshellId,
                EventName = "Fafnir",
                EventType = "HNM",
                StartTime = CampStart,
                EndTime = CampStart.AddHours(1),
                TimeStamp = CampStart.AddHours(1),
            });
        }

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
            DkpAmount = dkpAmount,
            PostedToSheetAt = CampStart.AddHours(2),
            PerCaptureDkp = priced,
            CampStartedAtUtc = priced ? CampStart : null,
            CampEndedAtUtc = priced ? CampStart.AddHours(1) : null,
            CampEventType = priced ? "HNM" : null,
            CampEventHistoryId = priced ? HistoryId : null,
        });

        var entryId = 1;
        for (var i = 0; i < captures.Length; i++)
        {
            var snapshotId = i + 1;
            db.AttendanceSnapshots.Add(new AttendanceSnapshot
            {
                Id = snapshotId,
                LinkshellId = LinkshellId,
                WindowEventId = CardId,
                CapturedAtUtc = CampStart.AddMinutes(10 * (i + 1)),
                SnapshotStatus = AttendanceSnapshotStatuses.Active,
                EntryCount = captures[i].Length,
            });
            foreach (var (name, amount) in captures[i])
            {
                db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry
                {
                    Id = entryId++,
                    SnapshotId = snapshotId,
                    CharacterName = name,
                    DkpAmount = amount,
                });
            }
        }

        await db.SaveChangesAsync();
        return db;
    }

    private static Task<List<DkpLedgerEntry>> EarnedAsync(ApplicationDbContext db, string appUserId) =>
        db.DkpLedgerEntries
            .Where(e => e.AppUserId == appUserId && e.EntryType == "SnapshotEarned")
            .ToListAsync();

    private static async Task<double> BalanceAsync(ApplicationDbContext db, string appUserId) =>
        (await db.AppUserLinkshells.SingleAsync(l => l.AppUserId == appUserId)).LinkshellDkp ?? 0d;

    [Fact]
    public async Task FlatCard_MainAndAltCaptured_PaysTheMemberOnce()
    {
        using var db = await SeededAsync(
            priced: false, dkpAmount: 4d,
            new[] { ("Alphalt", (double?)null), ("Bob", null) },
            new[] { ("Alpha", (double?)null) });

        await NewLedgerService(db).EnsurePostedWindowEventLedgerEntriesAsync(CardId, CancellationToken.None);

        var row = Assert.Single(await EarnedAsync(db, AlphaId));
        Assert.Equal(4d, row.Amount);
        Assert.Equal("Alpha", row.CharacterName);
        Assert.Equal(4d, await BalanceAsync(db, AlphaId));
        Assert.Equal(4d, Assert.Single(await EarnedAsync(db, BobId)).Amount);
    }

    // Alt in window 1 (2.5), main in window 2 (4): one row of 6.5, and the Past Event says 6.5 too
    // (it used to keep the larger name, 4, while the ledger paid 6.5).
    [Fact]
    public async Task PricedCamp_MainAndAltCaptured_OneRowWithTheSum_AndTheArchiveAgrees()
    {
        using var db = await SeededAsync(
            priced: true, dkpAmount: 1d,
            new[] { ("Alphalt", (double?)2.5), ("Bob", 2.5) },
            new[] { ("Alpha", (double?)4d), ("Bob", 4d) });

        await NewLedgerService(db).EnsurePostedWindowEventLedgerEntriesAsync(CardId, CancellationToken.None);

        var row = Assert.Single(await EarnedAsync(db, AlphaId));
        Assert.Equal(6.5d, row.Amount);
        Assert.Equal(6.5d, await BalanceAsync(db, AlphaId));

        var archive = await db.AppUserEventHistories.Where(r => r.EventHistoryId == HistoryId).ToListAsync();
        var alpha = Assert.Single(archive, r => r.AppUserId == AlphaId);
        Assert.Equal(6.5d, alpha.EventDkp);
        Assert.Equal("Alpha", alpha.CharacterName);
        Assert.Equal(6.5d, Assert.Single(archive, r => r.AppUserId == BobId).EventDkp);
    }

    // A second Post (or "Pay newly registered") pays nobody twice.
    [Fact]
    public async Task PostingTwice_DoesNotPayAgain()
    {
        using var db = await SeededAsync(
            priced: false, dkpAmount: 4d,
            new[] { ("Alphalt", (double?)null), ("Alpha", null) });
        var ledger = NewLedgerService(db);

        await ledger.EnsurePostedWindowEventLedgerEntriesAsync(CardId, CancellationToken.None);
        Assert.Equal(0, await ledger.EnsurePostedWindowEventLedgerEntriesAsync(CardId, CancellationToken.None));

        Assert.Single(await EarnedAsync(db, AlphaId));
        Assert.Equal(4d, await BalanceAsync(db, AlphaId));
    }

    [Fact]
    public async Task Update_LeavesAnAuditCorrectionUntouched()
    {
        using var db = await SeededAsync(
            priced: false, dkpAmount: 10d,
            new[] { ("Bob", (double?)null) });
        var ledger = NewLedgerService(db);
        await ledger.EnsurePostedWindowEventLedgerEntriesAsync(CardId, CancellationToken.None);

        // An officer audits Bob's 10 down to 8.
        var earned = Assert.Single(await EarnedAsync(db, BobId));
        var bob = await db.AppUserLinkshells.SingleAsync(l => l.AppUserId == BobId);
        db.DkpLedgerEntries.Add(new DkpLedgerEntry
        {
            AppUserId = BobId,
            LinkshellId = LinkshellId,
            EntryType = "AuditAdjustment",
            Amount = -2d,
            OccurredAt = CampStart.AddHours(3),
            EventType = earned.EventType,
            DkpPoolId = earned.DkpPoolId,
            SourceWindowEventId = CardId,
            AuditRelatedLedgerEntryId = earned.Id,
        });
        bob.LinkshellDkp = 8d;
        await db.SaveChangesAsync();

        // Someone edits the card (here: nothing about Bob) and presses Update.
        await ledger.ReconcilePostedWindowEventLedgerAsync(CardId, CancellationToken.None);

        var audit = await db.DkpLedgerEntries.SingleAsync(e => e.EntryType == "AuditAdjustment");
        Assert.Equal(-2d, audit.Amount);
        Assert.Equal(10d, Assert.Single(await EarnedAsync(db, BobId)).Amount);
        Assert.Equal(8d, await BalanceAsync(db, BobId));
    }

    // A retype moves the payout to the new pool, and every correction of it follows: the audit of
    // the payout AND an audit of that audit (the audit screen offers AuditAdjustment rows too).
    // Their amounts stay exactly as the officers left them.
    [Fact]
    public async Task Update_Retype_MovesEveryCorrectionWithThePayout()
    {
        using var db = await SeededAsync(
            priced: false, dkpAmount: 10d,
            new[] { ("Bob", (double?)null) });
        db.DkpPools.Add(new DkpPool { Id = 100, LinkshellId = LinkshellId, Name = "Main", IsDefault = true });
        db.DkpPools.Add(new DkpPool { Id = 101, LinkshellId = LinkshellId, Name = "Wyrms", SortOrder = 1 });
        db.DkpPoolEventTypes.Add(new DkpPoolEventType
        {
            LinkshellId = LinkshellId,
            DkpPoolId = 101,
            EventType = WindowEventEntryTypes.WyrmsCamp,
            NormalizedEventType = DkpPoolEventType.Normalize(WindowEventEntryTypes.WyrmsCamp),
        });
        await db.SaveChangesAsync();
        await NewLedgerService(db).EnsurePostedWindowEventLedgerEntriesAsync(CardId, CancellationToken.None);

        var earned = Assert.Single(await EarnedAsync(db, BobId));
        Assert.Equal(100, earned.DkpPoolId);
        var audit = new DkpLedgerEntry
        {
            AppUserId = BobId, LinkshellId = LinkshellId, EntryType = "AuditAdjustment", Amount = -2d,
            OccurredAt = CampStart.AddHours(3), EventType = earned.EventType, DkpPoolId = 100,
            SourceWindowEventId = CardId, AuditRelatedLedgerEntryId = earned.Id,
        };
        db.DkpLedgerEntries.Add(audit);
        await db.SaveChangesAsync();
        db.DkpLedgerEntries.Add(new DkpLedgerEntry
        {
            AppUserId = BobId, LinkshellId = LinkshellId, EntryType = "AuditAdjustment", Amount = 1d,
            OccurredAt = CampStart.AddHours(4), EventType = earned.EventType, DkpPoolId = 100,
            SourceWindowEventId = CardId, AuditRelatedLedgerEntryId = audit.Id,
        });
        (await db.WindowEvents.SingleAsync(w => w.Id == CardId)).EntryType = WindowEventEntryTypes.WyrmsCamp;
        await db.SaveChangesAsync();

        await NewLedgerService(db).ReconcilePostedWindowEventLedgerAsync(CardId, CancellationToken.None);

        var rows = await db.DkpLedgerEntries.Where(e => e.AppUserId == BobId).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(new[] { 10d, -2d, 1d }, rows.Select(r => r.Amount).ToArray());
        Assert.All(rows, r => Assert.Equal(101, r.DkpPoolId));
        Assert.All(rows, r => Assert.Equal(WindowEventEntryTypes.WyrmsCamp, r.EventType));
    }

    // A card posted before one-row-per-account can still hold a row per character. Update puts the
    // account's figure on one row and zeroes the other, instead of pricing both at the main's rate.
    [Fact]
    public async Task Update_OnALegacyTwoRowCard_PaysTheAccountOnce()
    {
        using var db = await SeededAsync(
            priced: true, dkpAmount: 1d,
            new[] { ("Alphalt", (double?)2.5) },
            new[] { ("Alpha", (double?)4d) });
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var writer = new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance);
        var alpha = await db.AppUserLinkshells.SingleAsync(l => l.AppUserId == AlphaId);
        foreach (var (name, amount) in new[] { ("Alphalt", 2.5d), ("Alpha", 4d) })
        {
            await writer.AppendAsync(
                alpha, "SnapshotEarned", amount, CampStart, DkpPoolRef.Derived(WindowEventEntryTypes.KingsCamp),
                new DkpEntryContext(CharacterName: "Alpha", SourceWindowEventId: CardId, EventHistoryId: HistoryId),
                CancellationToken.None);
        }
        db.AppUserEventHistories.Add(new AppUserEventHistory
        {
            EventHistoryId = HistoryId, AppUserId = AlphaId, CharacterName = "Alpha", EventDkp = 4d,
        });
        await db.SaveChangesAsync();

        await NewLedgerService(db).ReconcilePostedWindowEventLedgerAsync(CardId, CancellationToken.None);

        var rows = (await EarnedAsync(db, AlphaId)).OrderBy(r => r.Id).ToList();
        Assert.Equal(new[] { 6.5d, 0d }, rows.Select(r => r.Amount).ToArray());
        Assert.Equal(6.5d, await BalanceAsync(db, AlphaId));
        Assert.Equal(6.5d, (await db.AppUserEventHistories.SingleAsync(r => r.AppUserId == AlphaId)).EventDkp);
    }
}
