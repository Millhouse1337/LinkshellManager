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

// Deleting a Past Event has to STICK.
//
// A camp leaves two rows behind at End Camp: the Past Event, and the Window Event an officer
// reviews and Posts. Deleting the Past Event used to leave a POSTED Window Event standing (the FK
// is SetNull so that an unposted one survives, which is right -- its DKP is not paid yet). But
// WindowEventDkpLedgerService's per-linkshell sweep reads "posted with no archive" as a row from
// before archives existed and rebuilds the Past Event from the card's own copy of the camp:
// re-crediting its DKP and queuing an "event ended" summary to Discord for a camp that ended days
// ago. Seven deleted test camps came back that way the moment somebody opened the DKP page.
public class PastEventDeleteRemovesPostedCardTests
{
    private const int LinkshellId = 42;
    private const int HistoryId = 7;
    private static readonly DateTime CampStart = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (EventHistoryEditService Edits, WindowEventDkpLedgerService Ledger) NewServices(
        ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var writer = new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance);
        return (
            new EventHistoryEditService(db, writer, pools),
            new WindowEventDkpLedgerService(db, writer, pools, NullLogger<WindowEventDkpLedgerService>.Instance));
    }

    // The review card for the seeded camp. `posted` is whether an officer has Posted it;
    // `historyId` null is a card from before End Camp wrote archives (never linked to one).
    private static WindowEvent Card(int id, bool posted, int? historyId = HistoryId) => new()
    {
        Id = id,
        LinkshellId = LinkshellId,
        Name = "faff",
        Status = WindowEventStatuses.Open,
        CreatedAtUtc = CampStart,
        FirstCapturedAtUtc = CampStart,
        LastCapturedAtUtc = CampStart.AddMinutes(12),
        EntryType = WindowEventEntryTypes.KingsCamp,
        DkpAmount = 4d,
        PostedToSheetAt = posted ? CampStart.AddHours(1) : null,
        CampStartedAtUtc = CampStart,
        CampEndedAtUtc = CampStart.AddMinutes(12),
        CampEventType = "HNM",
        CampEventLocation = "Dragon's Aery",
        CampEventHistoryId = historyId,
    };

    // The linkshell, one archived camp, and each given card with one scanned member on it -- the
    // sweep ignores a card nobody was scanned in, so the member is what makes it a live subject.
    private static async Task<ApplicationDbContext> SeededAsync(params WindowEvent[] cards)
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.EventHistories.Add(new EventHistory
        {
            Id = HistoryId,
            LinkshellId = LinkshellId,
            EventName = "faff",
            EventType = "HNM",
            StartTime = CampStart,
            EndTime = CampStart.AddMinutes(12),
            TimeStamp = CampStart.AddMinutes(12),
        });

        var snapshotId = 1;
        foreach (var card in cards)
        {
            db.WindowEvents.Add(card);
            db.AttendanceSnapshots.Add(new AttendanceSnapshot
            {
                Id = snapshotId,
                LinkshellId = LinkshellId,
                WindowEventId = card.Id,
                CapturedAtUtc = CampStart.AddMinutes(5),
                CreatedAtUtc = CampStart.AddMinutes(5),
                SnapshotStatus = AttendanceSnapshotStatuses.Active,
                EntryCount = 1,
            });
            db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry
            {
                Id = snapshotId,
                SnapshotId = snapshotId,
                CharacterName = "Millhouse",
            });
            snapshotId++;
        }

        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task DeletingAPastEvent_DeletesThePostedCardBehindIt()
    {
        var db = await SeededAsync(Card(id: 1, posted: true));
        var (edits, _) = NewServices(db);

        Assert.True(await edits.DeleteEventAsync(HistoryId, CancellationToken.None));

        Assert.Empty(await db.EventHistories.ToListAsync());
        Assert.Empty(await db.WindowEvents.ToListAsync());
        Assert.Empty(await db.AttendanceSnapshots.ToListAsync());
        Assert.Empty(await db.AttendanceSnapshotEntries.ToListAsync());
    }

    [Fact]
    public async Task DeletingAPastEvent_LeavesAnUnpostedCardForTheOfficer()
    {
        var db = await SeededAsync(Card(id: 1, posted: false));
        var (edits, _) = NewServices(db);

        Assert.True(await edits.DeleteEventAsync(HistoryId, CancellationToken.None));

        Assert.Empty(await db.EventHistories.ToListAsync());
        var card = Assert.Single(await db.WindowEvents.ToListAsync());
        Assert.Null(card.PostedToSheetAt);
        Assert.Single(await db.AttendanceSnapshots.ToListAsync());
    }

    [Fact]
    public async Task TheLedgerSweep_DoesNotRebuildADeletedPastEvent()
    {
        var db = await SeededAsync(Card(id: 1, posted: true));
        var (edits, ledger) = NewServices(db);
        await edits.DeleteEventAsync(HistoryId, CancellationToken.None);

        // What loading the DKP page runs. Before the fix, this was the resurrection.
        await ledger.EnsurePostedWindowEventLedgerEntriesForLinkshellAsync(LinkshellId, CancellationToken.None);

        Assert.Empty(await db.EventHistories.ToListAsync());
    }

    // The backfill the sweep was written for is untouched: a card posted before archives existed
    // (never linked to one) still gets its Past Event on the next sweep.
    [Fact]
    public async Task TheLedgerSweep_StillBackfillsACardThatNeverHadAnArchive()
    {
        var db = await SeededAsync(Card(id: 1, posted: true, historyId: null));
        var (edits, ledger) = NewServices(db);
        // The seeded history is unrelated to this card; clear it so the only archive left is the
        // one the sweep builds.
        await edits.DeleteEventAsync(HistoryId, CancellationToken.None);
        Assert.Empty(await db.EventHistories.ToListAsync());

        await ledger.EnsurePostedWindowEventLedgerEntriesForLinkshellAsync(LinkshellId, CancellationToken.None);

        var rebuilt = Assert.Single(await db.EventHistories.ToListAsync());
        Assert.Equal("faff", rebuilt.EventName);
        var card = await db.WindowEvents.SingleAsync();
        Assert.Equal(rebuilt.Id, card.CampEventHistoryId);
    }
}
