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

// Past Events can't move DKP on an HNM camp's archive.
//
// A camp is paid from its review card as "SnapshotEarned" rows. The Past Events edits only see
// "EventEarned" rows, so on a camp they lied: Remove dropped the attendee but kept their DKP, Save
// changed only the label, and Add paid a second time on top of the card. Now those edits refuse a
// camp archive (any history a Window Event points at with CampEventHistoryId) and leave it exactly
// as it was. Timed events edit the same as before, and active credit still works on a camp.
public class PastEventCampArchiveReadOnlyTests
{
    private const int LinkshellId = 42;
    private const int CampHistoryId = 7;
    private const int TimedHistoryId = 8;
    private static readonly DateTime CampStart = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (EventHistoryEditService Edits, DkpLedgerWriter Writer) NewServices(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var writer = new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance);
        return (new EventHistoryEditService(db, writer, pools), writer);
    }

    // Alice attended (10 DKP, on both events); Bob is on the roster but on neither.
    // The camp archive has a posted review card pointing at it and its payout is SnapshotEarned;
    // the timed event has no card and its payout is EventEarned.
    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "alice", UserName = "alice" });
        db.Users.Add(new AppUser { Id = "bob", UserName = "bob" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = LinkshellId, AppUserId = "alice", CharacterName = "Alice", LinkshellDkp = 20
        });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 2, LinkshellId = LinkshellId, AppUserId = "bob", CharacterName = "Bob", LinkshellDkp = 0
        });

        foreach (var (id, name, type) in new[] { (CampHistoryId, "faff", "HNM"), (TimedHistoryId, "Sky", "Sky") })
        {
            db.EventHistories.Add(new EventHistory
            {
                Id = id,
                LinkshellId = LinkshellId,
                EventName = name,
                EventType = type,
                StartTime = CampStart,
                EndTime = CampStart.AddHours(1),
                TimeStamp = CampStart.AddHours(1),
                Duration = 1,
                DkpPerHour = type == "HNM" ? null : 10,
            });
            db.AppUserEventHistories.Add(new AppUserEventHistory
            {
                Id = id * 100,
                EventHistoryId = id,
                AppUserId = "alice",
                CharacterName = "Alice",
                Duration = 1,
                EventDkp = 10,
                ActiveCredit = true,
            });
            db.DkpLedgerEntries.Add(new DkpLedgerEntry
            {
                LinkshellId = LinkshellId,
                AppUserId = "alice",
                EventHistoryId = id,
                EntryType = type == "HNM" ? "SnapshotEarned" : "EventEarned",
                SourceWindowEventId = type == "HNM" ? 1 : null,
                Amount = 10,
                OccurredAt = CampStart.AddHours(1),
                CharacterName = "Alice",
                EventType = type,
            });
        }

        db.WindowEvents.Add(new WindowEvent
        {
            Id = 1,
            LinkshellId = LinkshellId,
            Name = "faff",
            Status = WindowEventStatuses.Open,
            CreatedAtUtc = CampStart,
            EntryType = WindowEventEntryTypes.KingsCamp,
            DkpAmount = 10d,
            PostedToSheetAt = CampStart.AddHours(2),
            CampStartedAtUtc = CampStart,
            CampEndedAtUtc = CampStart.AddHours(1),
            CampEventType = "HNM",
            CampEventHistoryId = CampHistoryId,
        });

        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<double> BalanceAsync(ApplicationDbContext db, string appUserId) =>
        (await db.AppUserLinkshells.AsNoTracking().SingleAsync(m => m.AppUserId == appUserId)).LinkshellDkp ?? 0;

    [Fact]
    public async Task CampWindowEventId_FindsTheCardOnlyForACampArchive()
    {
        var db = await SeededAsync();
        var (edits, _) = NewServices(db);

        Assert.Equal(1, await edits.CampWindowEventIdAsync(CampHistoryId, CancellationToken.None));
        Assert.Null(await edits.CampWindowEventIdAsync(TimedHistoryId, CancellationToken.None));
    }

    [Fact]
    public async Task SaveDkp_OnACampArchive_IsRefused_AndChangesNothing()
    {
        var db = await SeededAsync();
        var (edits, _) = NewServices(db);

        var outcome = await edits.SetParticipantDkpAsync(CampHistoryId, CampHistoryId * 100, 3, CancellationToken.None);

        Assert.Equal(EventHistoryEditOutcome.CampArchive, outcome);
        Assert.Equal(20, await BalanceAsync(db, "alice"));
        var row = await db.AppUserEventHistories.AsNoTracking().SingleAsync(p => p.Id == CampHistoryId * 100);
        Assert.Equal(10, row.EventDkp);
    }

    [Fact]
    public async Task Remove_OnACampArchive_IsRefused_AndKeepsTheAttendee()
    {
        var db = await SeededAsync();
        var (edits, _) = NewServices(db);

        var outcome = await edits.RemoveParticipantAsync(CampHistoryId, CampHistoryId * 100, CancellationToken.None);

        Assert.Equal(EventHistoryEditOutcome.CampArchive, outcome);
        Assert.True(await db.AppUserEventHistories.AnyAsync(p => p.Id == CampHistoryId * 100));
        Assert.Equal(20, await BalanceAsync(db, "alice"));
        Assert.Equal(2, await db.DkpLedgerEntries.CountAsync());
    }

    // The worst of the three: Add used to pay on top of the card, then the card's re-sync deleted
    // the attendee row, so an officer adding them "again" paid them twice.
    [Fact]
    public async Task Add_OnACampArchive_IsRefused_AndPaysNothing()
    {
        var db = await SeededAsync();
        var (edits, _) = NewServices(db);

        var outcome = await edits.AddParticipantAsync(
            CampHistoryId, "bob", 10, null, null, null, activeCredit: true, CancellationToken.None);

        Assert.Equal(EventHistoryEditOutcome.CampArchive, outcome);
        Assert.Equal(0, await BalanceAsync(db, "bob"));
        Assert.False(await db.DkpLedgerEntries.AnyAsync(e => e.AppUserId == "bob"));
        Assert.False(await db.AppUserEventHistories.AnyAsync(p => p.AppUserId == "bob"));
    }

    [Fact]
    public async Task EditEvent_OnACampArchive_RefusesANewRateOrDuration()
    {
        var db = await SeededAsync();
        var (edits, _) = NewServices(db);

        var newRate = await edits.EditEventAsync(CampHistoryId,
            new EventHistoryEditInput("renamed", "HNM", null, 1, 25), CancellationToken.None);
        var newDuration = await edits.EditEventAsync(CampHistoryId,
            new EventHistoryEditInput("renamed", "HNM", null, 2, null), CancellationToken.None);

        Assert.Equal(EventHistoryEditOutcome.CampArchive, newRate);
        Assert.Equal(EventHistoryEditOutcome.CampArchive, newDuration);
        // Refused before anything was touched -- the rename in the same request did not land.
        var history = await db.EventHistories.AsNoTracking().SingleAsync(h => h.Id == CampHistoryId);
        Assert.Equal("faff", history.EventName);
        Assert.Equal(1, history.Duration);
        Assert.Null(history.DkpPerHour);
    }

    // The form sends the stored Duration and rate back on every Save, so a plain rename of a camp
    // has to go through.
    [Fact]
    public async Task EditEvent_OnACampArchive_StillRenames()
    {
        var db = await SeededAsync();
        var (edits, _) = NewServices(db);

        var outcome = await edits.EditEventAsync(CampHistoryId,
            new EventHistoryEditInput("Fafnir", "HNM", "Dragon's Aery", 1, null), CancellationToken.None);

        Assert.Equal(EventHistoryEditOutcome.Done, outcome);
        var history = await db.EventHistories.AsNoTracking().SingleAsync(h => h.Id == CampHistoryId);
        Assert.Equal("Fafnir", history.EventName);
        Assert.Equal("Dragon's Aery", history.EventLocation);
        Assert.Equal(20, await BalanceAsync(db, "alice"));
    }

    [Fact]
    public async Task ActiveCredit_OnACampArchive_StillWorks()
    {
        var db = await SeededAsync();
        var (edits, _) = NewServices(db);

        Assert.True(await edits.SetParticipantActiveCreditAsync(
            CampHistoryId, CampHistoryId * 100, credited: false, CancellationToken.None));

        var row = await db.AppUserEventHistories.AsNoTracking().SingleAsync(p => p.Id == CampHistoryId * 100);
        Assert.False(row.ActiveCredit);
    }

    // A timed event has no review card, so its DKP still edits here exactly as before.
    [Fact]
    public async Task TimedEvent_StillEditsDkp()
    {
        var db = await SeededAsync();
        var (edits, _) = NewServices(db);

        Assert.Equal(EventHistoryEditOutcome.Done,
            await edits.SetParticipantDkpAsync(TimedHistoryId, TimedHistoryId * 100, 4, CancellationToken.None));
        Assert.Equal(14, await BalanceAsync(db, "alice"));

        Assert.Equal(EventHistoryEditOutcome.Done,
            await edits.AddParticipantAsync(TimedHistoryId, "bob", 10, null, null, null, activeCredit: true, CancellationToken.None));
        Assert.Equal(10, await BalanceAsync(db, "bob"));

        Assert.Equal(EventHistoryEditOutcome.Rejected,
            await edits.AddParticipantAsync(TimedHistoryId, "bob", 10, null, null, null, activeCredit: true, CancellationToken.None));
        Assert.Equal(10, await BalanceAsync(db, "bob"));
    }
}
