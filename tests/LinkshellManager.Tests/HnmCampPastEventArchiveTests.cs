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

// Ending an HNM camp has to leave a PAST EVENT behind.
//
// It did not. These camps hand their roster to the Event System page as a pending review row and
// the board itself is RECYCLED for the next pop rather than deleted — so between End Camp and an
// officer's Post the camp existed nowhere a member could see it: gone from the live list (that row
// is now the next pop), absent from Past Events, and recorded only as a review row. On a recurring
// board that gap runs for days, and a camp nobody ever got round to reviewing left no trace at all.
//
// The archive is written at End Camp now, from the camp's own proposed roster. Post still owns the
// money — it reconciles that archive to whatever the review settled on, and it must never file the
// camp a second time.
public class HnmCampPastEventArchiveTests
{
    private const int LinkshellId = 42;
    private const int EventId = 500;
    private const string AlphaId = "user-alpha";
    private const string BetaId = "user-beta";

    private static readonly DateTime CampStart = new(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static HnmCampReviewHandoffService NewHandoff(ApplicationDbContext db) =>
        new(db,
            new WdCampFinalizer(db, NullLogger<WdCampFinalizer>.Instance),
            new HnmStandardCampFinalizer(db, NullLogger<HnmStandardCampFinalizer>.Instance),
            NullLogger<HnmCampReviewHandoffService>.Instance);

    private static WindowEventDkpLedgerService NewLedgerService(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new WindowEventDkpLedgerService(
            db,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            NullLogger<WindowEventDkpLedgerService>.Instance);
    }

    // A live Standard camp mid-cycle. StartTime is deliberately the NEXT predicted repop rather
    // than when this camp began: that is the state End Camp leaves the recycled row in, and reading
    // it instead of CommencementStartTime is exactly how the archive would end up dated to a pop
    // that has not happened.
    private static Event LiveCamp() => new()
    {
        Id = EventId,
        LinkshellId = LinkshellId,
        EventName = "Fafnir/Nidhogg Day 3",
        EventType = "HNM",
        EventLocation = "Dragon's Aery",
        AssignedMonsterName = "Fafnir/Nidhogg",
        StartTime = CampStart.AddDays(3),
        CommencementStartTime = CampStart,
        EndTime = null,
        HnmWindowNumber = 2,
        WindowCountOverride = 7,
        CountsTowardActive = true,
    };

    // Seeds the linkshell, the camp, two members, and one scanned window per member.
    // `scans` maps AppUserId -> the character name the addon captured them under.
    private static async Task<ApplicationDbContext> SeededAsync(
        Dictionary<string, string>? scans = null, Event? camp = null)
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell
        {
            Id = LinkshellId,
            LinkshellName = "Test",
            EnableHnmSection = true,
            HnmStandardWindowBonus = 1,
            HnmStandardOpenBonus = 2,
            HnmStandardCloseBonus = 3,
        });
        db.Events.Add(camp ?? LiveCamp());

        db.Users.Add(new AppUser { Id = AlphaId, UserName = "alpha", CharacterName = "Alpha" });
        db.Users.Add(new AppUser { Id = BetaId, UserName = "beta", CharacterName = "Beta" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = LinkshellId, AppUserId = AlphaId, CharacterName = "Alpha",
        });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 2, LinkshellId = LinkshellId, AppUserId = BetaId, CharacterName = "Beta",
        });

        scans ??= new Dictionary<string, string> { [AlphaId] = "Alpha", [BetaId] = "Beta" };
        if (scans.Count > 0)
        {
            var window = new EventAttendanceWindow
            {
                Id = 1,
                EventId = EventId,
                SequenceNumber = 1,
                PostedAt = CampStart.AddMinutes(5),
            };
            db.EventAttendanceWindows.Add(window);

            var id = 1;
            foreach (var (appUserId, characterName) in scans)
            {
                db.AppUserEventWindows.Add(new AppUserEventWindow
                {
                    Id = id++,
                    EventAttendanceWindowId = window.Id,
                    AppUserId = appUserId,
                    CharacterName = characterName,
                    VerifiedAt = CampStart.AddMinutes(5),
                });
            }
        }

        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<WindowEvent?> EndCampAsync(ApplicationDbContext db)
    {
        var camp = await db.Events.FirstAsync(e => e.Id == EventId);
        var windowEvent = await NewHandoff(db).StageHandoffAsync(
            camp, popWindow: 2, claimed: false, killed: true, CancellationToken.None);
        await db.SaveChangesAsync();
        return windowEvent;
    }

    // ------------------------------------------------------------ the archive at End Camp ---

    [Fact]
    public async Task EndingACamp_ArchivesItAsAPastEventImmediately()
    {
        using var db = await SeededAsync();

        var windowEvent = await EndCampAsync(db);

        Assert.NotNull(windowEvent);
        var history = await db.EventHistories
            .Include(h => h.AppUserEventHistories)
            .SingleAsync();
        Assert.Equal("Fafnir/Nidhogg Day 3", history.EventName);
        Assert.Equal("HNM", history.EventType);
        Assert.Equal("Dragon's Aery", history.EventLocation);
        Assert.Equal(
            new[] { "Alpha", "Beta" },
            history.AppUserEventHistories.Select(p => p.CharacterName).OrderBy(n => n).ToArray());
    }

    // The link is what stops Post filing the camp a second time, and what lets the review's edits
    // find the row they are meant to correct.
    [Fact]
    public async Task TheReviewRowPointsAtTheArchiveItWrote()
    {
        using var db = await SeededAsync();

        var windowEvent = await EndCampAsync(db);

        var history = await db.EventHistories.SingleAsync();
        Assert.Equal(history.Id, windowEvent!.CampEventHistoryId);
    }

    // THE dating trap. The camp row is recycled, so by the time anything reads it StartTime points
    // at the NEXT pop — three days out in this fixture. An archive dated from it would file a past
    // event in the future.
    [Fact]
    public async Task TheArchiveIsDatedFromTheCamp_NotTheNextRepop()
    {
        using var db = await SeededAsync();

        await EndCampAsync(db);

        var history = await db.EventHistories.SingleAsync();
        Assert.Equal(CampStart, history.StartTime);
        Assert.Equal(CampStart, history.CommencementStartTime);
        Assert.Equal(CampStart.Date, history.StartDate);
        Assert.NotNull(history.EndTime);
        Assert.True(history.EndTime > CampStart, "the camp cannot have ended before it started");
    }

    // An empty camp leaves no review row, and so leaves no past event either — matching how an
    // ordinary event with nobody on it behaves. Archiving a camp nobody attended would put an
    // empty row in every member's history for a pop none of them were at.
    [Fact]
    public async Task ACampNobodyAttended_ArchivesNothing()
    {
        using var db = await SeededAsync(scans: new Dictionary<string, string>());

        var windowEvent = await EndCampAsync(db);

        Assert.Null(windowEvent);
        Assert.Empty(await db.EventHistories.ToListAsync());
    }

    // The proposal is what the review row opens with, so the archive quotes the same numbers rather
    // than sitting blank until somebody posts.
    [Fact]
    public async Task TheArchiveCarriesTheCampsProposedDkp()
    {
        using var db = await SeededAsync();

        await EndCampAsync(db);

        var history = await db.EventHistories.Include(h => h.AppUserEventHistories).SingleAsync();
        Assert.All(history.AppUserEventHistories, row => Assert.NotNull(row.EventDkp));
        Assert.All(history.AppUserEventHistories, row => Assert.True(row.ActiveCredit));
    }

    // ------------------------------------------------------------- what Post does with it ---

    private static async Task<int> PostAsync(ApplicationDbContext db, WindowEvent windowEvent)
    {
        windowEvent.PostedToSheetAt = DateTime.UtcNow;
        windowEvent.DkpAmount = 1d;
        windowEvent.EntryType = WindowEventEntryTypes.KingsCamp;
        await db.SaveChangesAsync();
        return await NewLedgerService(db)
            .EnsurePostedWindowEventLedgerEntriesAsync(windowEvent.Id, CancellationToken.None);
    }

    [Fact]
    public async Task PostingTheReview_DoesNotArchiveTheCampASecondTime()
    {
        using var db = await SeededAsync();
        var windowEvent = await EndCampAsync(db);

        await PostAsync(db, windowEvent!);

        Assert.Single(await db.EventHistories.ToListAsync());
    }

    // The whole point of review: the officer changed what somebody is owed, and the past event has
    // to say the same thing the ledger does.
    [Fact]
    public async Task PostingTheReview_WritesTheReviewedAmountOntoTheArchive()
    {
        using var db = await SeededAsync();
        var windowEvent = await EndCampAsync(db);

        // End Camp already staged one override per member (the camp's proposal), so an officer
        // raising somebody's amount EDITS that row. Adding a second would not be the same test:
        // the amount lookup folds duplicates first-wins, so the new row would simply be ignored.
        var override_ = await db.WindowEventMemberDkps
            .FirstAsync(o => o.WindowEventId == windowEvent!.Id && o.CharacterName == "Alpha");
        override_.DkpAmount = 9d;
        await db.SaveChangesAsync();

        await PostAsync(db, windowEvent!);

        var history = await db.EventHistories.Include(h => h.AppUserEventHistories).SingleAsync();
        var alpha = history.AppUserEventHistories.Single(p => p.CharacterName == "Alpha");
        Assert.Equal(9d, alpha.EventDkp);
    }

    // Somebody the officer struck off during review must not stay credited in the archive. The
    // proposal said they were there; the review said no, and the review is the answer.
    [Fact]
    public async Task PostingTheReview_DropsAnyoneTheOfficerRemoved()
    {
        using var db = await SeededAsync();
        var windowEvent = await EndCampAsync(db);

        var beta = await db.AttendanceSnapshotEntries.FirstAsync(e => e.CharacterName == "Beta");
        db.AttendanceSnapshotEntries.Remove(beta);
        await db.SaveChangesAsync();

        await PostAsync(db, windowEvent!);

        var history = await db.EventHistories.Include(h => h.AppUserEventHistories).SingleAsync();
        Assert.Equal(
            new[] { "Alpha" },
            history.AppUserEventHistories.Select(p => p.CharacterName).ToArray());
    }

    // Someone added during review is on the camp as far as everyone is concerned, so the archive
    // has to grow to match.
    [Fact]
    public async Task PostingTheReview_AddsAnyoneTheOfficerAdded()
    {
        using var db = await SeededAsync(
            scans: new Dictionary<string, string> { [AlphaId] = "Alpha" });
        var windowEvent = await EndCampAsync(db);

        var snapshot = await db.AttendanceSnapshots.FirstAsync();
        db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry
        {
            SnapshotId = snapshot.Id,
            CharacterName = "Beta",
        });
        await db.SaveChangesAsync();

        await PostAsync(db, windowEvent!);

        var history = await db.EventHistories.Include(h => h.AppUserEventHistories).SingleAsync();
        Assert.Equal(
            new[] { "Alpha", "Beta" },
            history.AppUserEventHistories.Select(p => p.CharacterName).OrderBy(n => n).ToArray());
    }

    // AppUserEventHistory is uniquely indexed on (EventHistoryId, AppUserId). A member captured on
    // their main AND added by hand under an alt is two roster entries for ONE account — matching on
    // character name would insert a second row and the save would throw, taking the whole Post down.
    [Fact]
    public async Task OneAccountScannedUnderTwoNames_KeepsOneArchiveRow()
    {
        using var db = await SeededAsync(
            scans: new Dictionary<string, string> { [AlphaId] = "Alpha" });
        var alpha = await db.Users.FirstAsync(u => u.Id == AlphaId);
        alpha.AltCharacterName1 = "Alphalt";
        var windowEvent = await EndCampAsync(db);

        var snapshot = await db.AttendanceSnapshots.FirstAsync();
        db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry
        {
            SnapshotId = snapshot.Id,
            CharacterName = "Alphalt",
        });
        await db.SaveChangesAsync();

        await PostAsync(db, windowEvent!);

        var history = await db.EventHistories.Include(h => h.AppUserEventHistories).SingleAsync();
        Assert.Single(history.AppUserEventHistories);
        Assert.Equal(AlphaId, history.AppUserEventHistories.Single().AppUserId);
    }

    // ------------------------------------------------------- the camp's Claim Shield rows ---

    // THE double-pay. An HNM board is RECYCLED for the next pop rather than deleted, and both
    // finalizers read the claim bonus off `Capture.EventId == ev.Id` with no time bound — so a
    // capture left pointing at the board was counted again on the NEXT camp, and the one after
    // that. Everyone who tagged one pop kept earning the claim bonus on every later pop of the
    // same board.
    [Fact]
    public async Task EndingACamp_MovesItsLotteriesOntoTheArchive()
    {
        using var db = await SeededAsync();
        db.ClaimShieldCaptures.Add(new ClaimShieldCapture
        {
            Id = 1,
            LinkshellId = LinkshellId,
            EventId = EventId,
            MonsterName = "Nidhogg",
            Won = true,
            TotalPlayers = 20,
            CapturedAtUtc = CampStart.AddMinutes(30),
            CreatedAtUtc = CampStart.AddMinutes(30),
        });
        await db.SaveChangesAsync();

        await EndCampAsync(db);

        var history = await db.EventHistories.SingleAsync();
        var capture = await db.ClaimShieldCaptures.SingleAsync();
        Assert.Null(capture.EventId);
        Assert.Equal(history.Id, capture.EventHistoryId);
    }

    // A camp nobody attended writes no archive, so there is nothing to hand the lottery to — but it
    // still must not follow the board into the next pop. Detached and kept as a linkshell record,
    // which is where a capture taken with no camp open already lives.
    [Fact]
    public async Task ACampNobodyAttended_StillDetachesItsLotteries()
    {
        using var db = await SeededAsync(scans: new Dictionary<string, string>());
        db.ClaimShieldCaptures.Add(new ClaimShieldCapture
        {
            Id = 1,
            LinkshellId = LinkshellId,
            EventId = EventId,
            MonsterName = "Nidhogg",
            Won = false,
            TotalPlayers = 20,
            CapturedAtUtc = CampStart.AddMinutes(30),
            CreatedAtUtc = CampStart.AddMinutes(30),
        });
        await db.SaveChangesAsync();

        Assert.Null(await EndCampAsync(db));

        var capture = await db.ClaimShieldCaptures.SingleAsync();
        Assert.Null(capture.EventId);
        Assert.Null(capture.EventHistoryId);
    }

    // Review rows staged BEFORE the archive moved to End Camp have no history to reconcile. They
    // must still archive on Post, or every camp sitting unposted at deploy time would lose its past
    // event outright.
    [Fact]
    public async Task ALegacyReviewRowWithNoArchive_StillGetsOneAtPost()
    {
        using var db = await SeededAsync();
        var windowEvent = await EndCampAsync(db);

        // Rewind to the pre-change state: the review row exists, the archive does not.
        var staged = await db.EventHistories.Include(h => h.AppUserEventHistories).SingleAsync();
        db.AppUserEventHistories.RemoveRange(staged.AppUserEventHistories);
        db.EventHistories.Remove(staged);
        windowEvent!.CampEventHistoryId = null;
        windowEvent.CampEventHistory = null;
        await db.SaveChangesAsync();
        Assert.Empty(await db.EventHistories.ToListAsync());

        await PostAsync(db, windowEvent);

        var history = await db.EventHistories.Include(h => h.AppUserEventHistories).SingleAsync();
        Assert.Equal(2, history.AppUserEventHistories.Count);
        Assert.Equal(history.Id, windowEvent.CampEventHistoryId);
    }
}
