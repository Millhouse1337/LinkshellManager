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

// A camp's money belongs to its CAPTURES, not to one number per member.
//
// The review card renders a DKP box on every capture a person appears in, and every one of them
// used to be seeded from the same per-member total: a member owed 2 for the whole camp read as 2 in
// window 1, 2 in window 2 and 2 in window 3. Editing them was worse than the display — all three
// posted under the same character name, and the last one submitted won, so raising window 1 was
// silently undone by the untouched box further down the card.
//
// Each capture now carries what THAT capture pays — the open, a regular window, the close, and the
// kill post carrying the kill bonus — and a member is owed the sum. The tag bonus is its own "Tag"
// capture, because its evidence is the addon tag list rather than a roster scan: a tagger can earn
// it having appeared in no window at all. The sum is exactly the number the finalizer computed.
public class HnmCampCapturePricingTests
{
    private const int LinkshellId = 21;
    private const int EventId = 700;
    private const string AlphaId = "user-alpha";
    private const string BetaId = "user-beta";

    private static readonly DateTime CampStart = new(2026, 9, 3, 1, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static HnmCampReviewHandoffService NewHandoff(ApplicationDbContext db) =>
        new(db,
            new WdCampFinalizer(db, NullLogger<WdCampFinalizer>.Instance),
            new HnmStandardCampFinalizer(db, NullLogger<HnmStandardCampFinalizer>.Instance),
            new HnmAutoEventService(db, NullLogger<HnmAutoEventService>.Instance),
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

    // Open 1, regular window 0.5, close 2, claim 3, kill 4 — five distinct numbers, so a capture
    // seeded from the wrong one is unmistakable.
    private static Linkshell TestLinkshell() => new()
    {
        Id = LinkshellId,
        LinkshellName = "Test",
        EnableHnmSection = true,
        HnmStandardOpenBonus = 1,
        HnmStandardWindowBonus = 0.5,
        HnmStandardCloseBonus = 2,
        HnmStandardClaimBonus = 3,
        HnmStandardKillBonus = 4,
    };

    private static Event LiveCamp(string? attendanceMode = null) => new()
    {
        Id = EventId,
        LinkshellId = LinkshellId,
        EventName = "Fafnir/Nidhogg D3",
        EventType = "HNM",
        EventLocation = "Dragon's Aery",
        AssignedMonsterName = "Fafnir/Nidhogg",
        StartTime = CampStart,
        CommencementStartTime = CampStart,
        EndTime = null,
        HnmWindowNumber = 3,
        WindowCountOverride = 7,
        AttendanceMode = attendanceMode,
    };

    // Three posted windows. Alpha is scanned in all three; Beta only in the middle one, which is
    // what proves a capture pays the person who was in IT rather than the camp's headline rate.
    //
    // Window 3 is marked as the close, so the grid is open / regular / close = 1 / 0.5 / 2.
    private static async Task<ApplicationDbContext> SeededAsync(
        string? attendanceMode = null, bool markClose = true, bool duplicateAlphaInWindowOne = false)
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(TestLinkshell());
        db.Events.Add(LiveCamp(attendanceMode));

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

        var scanId = 1;
        void Scan(int windowId, string appUserId, string characterName)
        {
            db.AppUserEventWindows.Add(new AppUserEventWindow
            {
                Id = scanId++,
                EventAttendanceWindowId = windowId,
                AppUserId = appUserId,
                CharacterName = characterName,
                VerifiedAt = CampStart.AddMinutes(windowId * 5),
            });
        }

        foreach (var sequence in new[] { 1, 2, 3 })
        {
            db.EventAttendanceWindows.Add(new EventAttendanceWindow
            {
                Id = sequence,
                EventId = EventId,
                SequenceNumber = sequence,
                PostedAt = CampStart.AddMinutes(sequence * 5),
                IsClosingWindow = markClose && sequence == 3,
            });
            Scan(sequence, AlphaId, "Alpha");
        }
        Scan(2, BetaId, "Beta");

        // Manual Check In pays the check-in RANGE off the participation, not the window scans, so
        // that mode needs a checked-in roster or the camp hands off with nobody on it.
        if (attendanceMode == HnmAttendanceModes.Wd)
        {
            var participationId = 1;
            foreach (var (appUserId, characterName) in
                     new[] { (AlphaId, "Alpha"), (BetaId, "Beta") })
            {
                db.AppUserEvents.Add(new AppUserEvent
                {
                    Id = participationId++,
                    EventId = EventId,
                    AppUserId = appUserId,
                    CharacterName = characterName,
                    WdArrivalWindow = 1,
                    WdDepartureWindow = 3,
                });
            }
        }

        if (duplicateAlphaInWindowOne)
        {
            // The same account caught twice in one window — two participations, one night. The
            // finalizer sums over the SEQUENCES a member was seen in, so this must still pay once.
            Scan(1, AlphaId, "Alpha");
        }

        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<WindowEvent?> EndCampAsync(
        ApplicationDbContext db, bool claimed = false, bool killed = true)
    {
        var camp = await db.Events.FirstAsync(e => e.Id == EventId);
        var windowEvent = await NewHandoff(db).StageHandoffAsync(
            camp, popWindow: 3, claimed, killed, CancellationToken.None);
        await db.SaveChangesAsync();
        return windowEvent;
    }

    private static async Task<Dictionary<string, double?>> AmountsByWindowAsync(
        ApplicationDbContext db, string characterName)
    {
        var snapshots = await db.AttendanceSnapshots.Include(s => s.Entries).ToListAsync();
        return snapshots
            .Where(s => s.Entries.Any(e => e.CharacterName == characterName))
            .ToDictionary(
                s => s.Name ?? "?",
                s => s.Entries.First(e => e.CharacterName == characterName).DkpAmount);
    }

    // ------------------------------------------------------------------ what each window pays ---

    // The heart of it: three captures, three different numbers, none of them the member's total.
    [Fact]
    public async Task EndingACamp_PricesEachCaptureAsItsOwnWindow()
    {
        using var db = await SeededAsync();

        await EndCampAsync(db);

        var byWindow = await AmountsByWindowAsync(db, "Alpha");
        Assert.Equal(1d, byWindow["Open"]);         // sequence 1
        Assert.Equal(0.5d, byWindow["Window 2"]);   // a regular window
        Assert.Equal(2d, byWindow["Close"]);        // the marked closing window
    }

    // Each capture is NAMED for what it is paid as. "Window 1, Window 2, Window 3" a minute apart
    // never said which one earned the open and which the close, which is the first thing an officer
    // reviewing the money looks for.
    [Fact]
    public async Task EndingACamp_NamesEachCaptureForTheRoleItIsPaidAs()
    {
        using var db = await SeededWithKillPostAndTagAsync();

        await EndCampAsync(db, claimed: true, killed: true);

        var names = (await AmountsByWindowAsync(db, "Alpha")).Keys.OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "Close", "Kill", "Open", "Tag", "Window 2" }, names);
    }

    // The kill post is its own capture and pays the KILL bonus. It is priced at 0 as a window
    // (WindowValue) precisely because it is not a roster read of the camp — it records who was
    // standing there when the mob died, and this bonus is what pays for that. Leaving the 0 on it
    // made the kill post look like a capture that pays nothing.
    [Fact]
    public async Task TheKillPost_CarriesTheKillBonus()
    {
        using var db = await SeededWithKillPostAndTagAsync();

        await EndCampAsync(db, claimed: true, killed: true);

        var byWindow = await AmountsByWindowAsync(db, "Alpha");
        Assert.Equal(4d, byWindow["Kill"]);   // the kill bonus, not a window rate
    }

    // ...and the Tag capture is left holding ONLY the tag bonus. The two are separate things
    // earned by separate evidence — a tag list and a kill roster — so one capture carrying both
    // could not say which of them a number came from.
    [Fact]
    public async Task TheTagCapture_CarriesTheTagBonusAlone()
    {
        using var db = await SeededWithKillPostAndTagAsync();

        await EndCampAsync(db, claimed: true, killed: true);

        var byWindow = await AmountsByWindowAsync(db, "Alpha");
        Assert.Equal(3d, byWindow["Tag"]);   // the tag bonus, with the kill bonus on its own row
    }

    // Two kill posts, one kill bonus. A camp holds more than one when the officer posted, spotted a
    // miss and posted again; the finalizer pays for being in the roster AT ALL, so the second must
    // carry nothing rather than paying the bonus twice.
    [Fact]
    public async Task ASecondKillPost_DoesNotPayTheBonusTwice()
    {
        using var db = await SeededWithKillPostAndTagAsync();
        db.EventAttendanceWindows.Add(new EventAttendanceWindow
        {
            Id = 5,
            EventId = EventId,
            SequenceNumber = 5,
            PostedAt = CampStart.AddMinutes(30),
            IsKillWindow = true,
        });
        db.AppUserEventWindows.Add(new AppUserEventWindow
        {
            Id = 98,
            EventAttendanceWindowId = 5,
            AppUserId = AlphaId,
            CharacterName = "Alpha",
            VerifiedAt = CampStart.AddMinutes(30),
        });
        await db.SaveChangesAsync();

        await EndCampAsync(db, claimed: true, killed: true);

        var killTotal = (await db.AttendanceSnapshots.Include(s => s.Entries).ToListAsync())
            .Where(s => s.Name == "Kill")
            .SelectMany(s => s.Entries)
            .Where(e => e.CharacterName == "Alpha")
            .Sum(e => e.DkpAmount ?? 0d);
        Assert.Equal(4d, killTotal);
    }

    // The full shape: three ordinary windows, a kill post Alpha is in, and a tag Alpha earned.
    private static async Task<ApplicationDbContext> SeededWithKillPostAndTagAsync()
    {
        var db = await SeededAsync();
        db.EventAttendanceWindows.Add(new EventAttendanceWindow
        {
            Id = 4,
            EventId = EventId,
            SequenceNumber = 4,
            PostedAt = CampStart.AddMinutes(25),
            IsKillWindow = true,
        });
        db.AppUserEventWindows.Add(new AppUserEventWindow
        {
            Id = 99,
            EventAttendanceWindowId = 4,
            AppUserId = AlphaId,
            CharacterName = "Alpha",
            VerifiedAt = CampStart.AddMinutes(25),
        });
        db.ClaimShieldCaptures.Add(new ClaimShieldCapture { Id = 1, LinkshellId = LinkshellId, EventId = EventId });
        db.ClaimShieldCaptureMembers.Add(new ClaimShieldCaptureMember
        {
            Id = 1, CaptureId = 1, AppUserId = AlphaId, CharacterName = "Alpha",
        });
        await db.SaveChangesAsync();
        return db;
    }

    // A capture pays the person who was in IT. Beta only ever appeared in the middle window.
    [Fact]
    public async Task EndingACamp_PaysAMemberOnlyForTheWindowsTheyWereScannedIn()
    {
        using var db = await SeededAsync();

        await EndCampAsync(db);

        var byWindow = await AmountsByWindowAsync(db, "Beta");
        Assert.Equal(new[] { "Window 2" }, byWindow.Keys.ToArray());
        Assert.Equal(0.5d, byWindow["Window 2"]);
    }

    // The claim and the kill are not window credit — the Post Kill roster is priced at 0 as a
    // window precisely because the kill bonus is what pays for standing there. They ride on their
    // own capture so a window's number stays purely what that window pays.
    [Fact]
    public async Task EndingAClaimedCamp_FilesTheBonusesAsTheirOwnCapture()
    {
        using var db = await SeededAsync();
        db.ClaimShieldCaptures.Add(new ClaimShieldCapture { Id = 1, LinkshellId = LinkshellId, EventId = EventId });
        db.ClaimShieldCaptureMembers.Add(new ClaimShieldCaptureMember
        {
            Id = 1, CaptureId = 1, AppUserId = AlphaId, CharacterName = "Alpha",
        });
        await db.SaveChangesAsync();

        await EndCampAsync(db, claimed: true, killed: true);

        var byWindow = await AmountsByWindowAsync(db, "Alpha");
        // Claimed, and nobody was scanned in a kill window, so this is the claim bonus alone.
        Assert.Equal(3d, byWindow["Tag"]);
        // Beta tagged nothing, so there is nothing to file for them.
        Assert.DoesNotContain("Tag", (await AmountsByWindowAsync(db, "Beta")).Keys);
    }

    // The contract that makes any of this safe to deploy: whatever the captures are seeded with,
    // they add up to the number the finalizer already decided on.
    [Fact]
    public async Task TheCapturesSumToWhatTheFinalizerComputed()
    {
        using var db = await SeededAsync();
        db.ClaimShieldCaptures.Add(new ClaimShieldCapture { Id = 1, LinkshellId = LinkshellId, EventId = EventId });
        db.ClaimShieldCaptureMembers.Add(new ClaimShieldCaptureMember
        {
            Id = 1, CaptureId = 1, AppUserId = AlphaId, CharacterName = "Alpha",
        });
        await db.SaveChangesAsync();

        var camp = await db.Events.FirstAsync(e => e.Id == EventId);
        var linkshell = await db.Linkshells.FirstAsync(l => l.Id == LinkshellId);
        var expected = (await new HnmStandardCampFinalizer(db, NullLogger<HnmStandardCampFinalizer>.Instance)
                .BuildRosterAsync(camp, linkshell, popWindow: 3, claimed: true, killed: true, CancellationToken.None))
            .ToDictionary(m => m.CharacterName, m => m.Dkp);

        await EndCampAsync(db, claimed: true, killed: true);

        var snapshots = await db.AttendanceSnapshots.Include(s => s.Entries).ToListAsync();
        var summed = WindowEventCaptureDkp.SumByCharacter(snapshots);
        Assert.Equal(expected["Alpha"], summed["Alpha"], 4);
        Assert.Equal(expected["Beta"], summed["Beta"], 4);
    }

    // One window pays a member ONCE however many scan rows they have in it. The duplicate row is
    // still shown — it is what was captured — it just carries nothing.
    [Fact]
    public async Task ASecondScanInTheSameWindow_IsNotPaidTwice()
    {
        using var db = await SeededAsync(duplicateAlphaInWindowOne: true);

        await EndCampAsync(db);

        var windowOne = await db.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstAsync(s => s.Name == "Open");
        var alphaRows = windowOne.Entries.Where(e => e.CharacterName == "Alpha").ToList();
        Assert.Equal(2, alphaRows.Count);
        Assert.Equal(1d, alphaRows.Sum(e => e.DkpAmount ?? 0d));
    }

    // --------------------------------------------------------------- what the card and Post read ---

    [Fact]
    public async Task TheRosterReportsTheSumOfAMembersCaptures()
    {
        using var db = await SeededAsync();
        var windowEvent = await EndCampAsync(db);

        var loaded = await db.WindowEvents
            .Include(w => w.Snapshots).ThenInclude(s => s.Entries)
            .Include(w => w.MemberDkpOverrides)
            .FirstAsync(w => w.Id == windowEvent!.Id);
        var row = AttendanceSectionsBuilder
            .MapWindowEvent(loaded, NodaTime.DateTimeZone.Utc)
            .CombinedMembers
            .Single(m => m.CharacterName == "Alpha");

        Assert.Equal(3.5d, row.EffectiveDkpAmount);   // 1 + 0.5 + 2
    }

    // Re-pricing ONE window is the whole point of editing on the capture, and it has to reach the
    // ledger rather than being averaged away or overwritten.
    [Fact]
    public async Task PostingCreditsTheSumOfTheCaptures_IncludingAnEditedWindow()
    {
        using var db = await SeededAsync();
        var windowEvent = await EndCampAsync(db);

        var openWindow = await db.AttendanceSnapshots
            .Include(s => s.Entries)
            .FirstAsync(s => s.Name == "Open");
        openWindow.Entries.First(e => e.CharacterName == "Alpha").DkpAmount = 5d;

        windowEvent!.PostedToSheetAt = DateTime.UtcNow;
        windowEvent.EntryType = WindowEventEntryTypes.KingsCamp;
        await db.SaveChangesAsync();

        await NewLedgerService(db).EnsurePostedWindowEventLedgerEntriesAsync(
            windowEvent.Id, CancellationToken.None);

        var alpha = await db.DkpLedgerEntries.FirstAsync(e => e.AppUserId == AlphaId);
        Assert.Equal(7.5d, alpha.Amount);   // 5 + 0.5 + 2
    }

    // The handoff writes no per-member rows on a priced camp: two copies of one payout, and the one
    // nobody edits is the one that goes stale.
    [Fact]
    public async Task APricedCamp_KeepsItsMoneyInOnePlace()
    {
        using var db = await SeededAsync();

        var windowEvent = await EndCampAsync(db);

        Assert.True(windowEvent!.PerCaptureDkp);
        Assert.Empty(await db.WindowEventMemberDkps.ToListAsync());
    }

    // ------------------------------------------------------------------- Manual Check In camps ---

    // Manual Check In credit comes from the check-in RANGE, so a member is paid for windows that
    // have no capture at all and there is no honest per-capture number to write. Those camps keep
    // paying per member, exactly as they did.
    [Fact]
    public async Task AManualCheckInCamp_IsStillPricedPerMember()
    {
        using var db = await SeededAsync(attendanceMode: HnmAttendanceModes.Wd);

        var windowEvent = await EndCampAsync(db);

        Assert.False(windowEvent!.PerCaptureDkp);
        Assert.NotEmpty(await db.WindowEventMemberDkps.ToListAsync());
        Assert.All(
            await db.AttendanceSnapshotEntries.ToListAsync(),
            entry => Assert.Null(entry.DkpAmount));
    }
}
