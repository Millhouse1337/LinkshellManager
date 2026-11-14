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

namespace LinkshellManager.PostgresTests;

// SAFETY NET for breaking up HnmCampReviewHandoffService.StageHandoffAsync (the End Camp payout).
//
// Each test runs a realistic camp through End Camp on the real database and compares EVERY row of
// EVERY table with a saved copy in Snapshots/EndCamp/<test>.txt: the review card and its captures
// (with each person's DKP), the Past Event and its participants, the archived windows, Claim Shield
// captures, loot charges, ledger rows, balances and the board. Most tests then Post the card and
// compare again, so the ledger rows and balances the camp pays are pinned too.
//
// These tests describe what the code does TODAY. If one fails after a refactor, the refactor
// changed behaviour. Only regenerate the files (see DatabaseStateDump.AssertMatchesSnapshot) when a
// change is meant, and read the diff.
//
// The finer rules (one capture per window, the kill bonus, misc pricing...) have their own small
// tests in HnmCampCapturePricingTests and HnmCampPastEventArchiveTests; these are the whole picture.
[Collection("postgres")]
public class EndCampCharacterizationTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    // Every time in the seed is a whole number of minutes from here, and End Camp's "now" lands on
    // T0+0m, so the saved files are the same on every run.
    private readonly DateTime _t0 = DateTime.UtcNow;

    public EndCampCharacterizationTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // ------------------------------------------------------------------------------- services ---

    private static HnmCampReviewHandoffService NewHandoff(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new HnmCampReviewHandoffService(
            db,
            new WdCampFinalizer(db, NullLogger<WdCampFinalizer>.Instance),
            new HnmStandardCampFinalizer(db, NullLogger<HnmStandardCampFinalizer>.Instance),
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            NullLogger<HnmCampReviewHandoffService>.Instance);
    }

    // The board's End Camp form (Activity and Discord both land here).
    private static HnmCampPopService NewPop(ApplicationDbContext db) => new(
        db,
        NewHandoff(db),
        new MonsterTimingResolver(db),
        new TodRecorder(db, new MonsterTimingResolver(db), NullLogger<TodRecorder>.Instance),
        NullLogger<HnmCampPopService>.Instance);

    private static WindowEventDkpLedgerService NewLedger(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new WindowEventDkpLedgerService(
            db,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            NullLogger<WindowEventDkpLedgerService>.Instance);
    }

    private async Task<HnmCampPopService.Result> EndCampFromTheBoardAsync(
        int boardId, bool claimed, bool killed, int popWindow, bool hq = false, int? dayNumber = null)
    {
        await using var db = _pg.CreateContext();
        var result = await NewPop(db).PopAsync(
            new HnmCampPopService.Request(
                boardId,
                TodTimeUtc: _t0.AddMinutes(-2),
                Cooldown: null,
                Interval: null,
                DayNumber: dayNumber,
                Claimed: claimed,
                Killed: killed,
                PopWindow: popWindow,
                Hq: hq),
            CancellationToken.None);
        Assert.True(result.Success, result.Error);
        return result;
    }

    // The addon's End Event (and the generic End Event actions).
    private async Task EndCampFromTheAddonAsync(int boardId)
    {
        await using var db = _pg.CreateContext();
        Assert.True(await NewHandoff(db).HandOffAndRecycleAsync(boardId, CancellationToken.None));
    }

    // An officer presses Post on every review card still pending, oldest first.
    private async Task PostEveryPendingCardAsync()
    {
        await using var db = _pg.CreateContext();
        var cards = await db.WindowEvents
            .Where(card => card.PostedToSheetAt == null)
            .OrderBy(card => card.Id)
            .ToListAsync();
        Assert.NotEmpty(cards);
        foreach (var card in cards)
        {
            Assert.NotNull(await NewLedger(db).PostAndPayAsync(card, _t0.AddMinutes(30), CancellationToken.None));
        }
    }

    private async Task<string> DumpAsync()
    {
        await using var db = _pg.CreateContext();
        return await DatabaseStateDump.DumpAsync(db, _t0);
    }

    // ------------------------------------------------------------------------------- the seed ---

    private sealed record Seeded(int LinkshellId, int BoardId, int MainPoolId, int HnmPoolId);

    // A linkshell with six distinct HNM rates (so a number paid from the wrong one stands out), a
    // Main pool and an HNM pool that HNM events pay into, and five members:
    //   Alpha  -- a registered main
    //   Beta   -- registered, scanned on an alt ("Betalt")
    //   Gamma  -- an imported placeholder account
    //   Delta  -- registered, only ever tags the mob
    //   Echo   -- registered, only on a misc post
    // plus "Stranger", scanned but not registered at all.
    private async Task<Seeded> SeedLinkshellAsync(
        ApplicationDbContext db, string attendanceMode = HnmAttendanceModes.Standard, string lootStructure = "Dkp")
    {
        var linkshell = new Linkshell
        {
            LinkshellName = "Characterization LS",
            LootStructure = lootStructure,
            HnmAttendanceMode = attendanceMode,
            HnmStandardOpenBonus = 1,
            HnmStandardWindowBonus = 0.5,
            HnmStandardMiscBonus = 0.75,
            HnmStandardCloseBonus = 2,
            HnmStandardClaimBonus = 3,
            HnmStandardKillBonus = 4,
            WdDkpPerWindow = 0.5,
            WdOpenBonus = 1,
            WdCloseBonus = 2,
            WdClaimBonus = 3,
            WdKillBonus = 4,
        };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();

        var main = DkpPoolDefaults.BuildDefaultPool(linkshell.Id);
        main.CreatedAtUtc = _t0.AddDays(-30);
        var hnm = new DkpPool
        {
            LinkshellId = linkshell.Id, Name = "HNM", SortOrder = 1, Accent = DkpPoolAccents.Default,
            CreatedAtUtc = _t0.AddDays(-30),
        };
        db.DkpPools.AddRange(main, hnm);
        await db.SaveChangesAsync();
        db.DkpPoolEventTypes.Add(new DkpPoolEventType
        {
            LinkshellId = linkshell.Id, DkpPoolId = hnm.Id, EventType = "HNM",
            NormalizedEventType = DkpPoolEventType.Normalize("HNM"),
        });

        db.Users.AddRange(
            new AppUser { Id = "alpha", UserName = "alpha", CharacterName = "Alpha" },
            new AppUser { Id = "beta", UserName = "beta", CharacterName = "Beta", AltCharacterName1 = "Betalt" },
            new AppUser { Id = "gamma", UserName = "gamma", CharacterName = "Gamma", IsPlaceholder = true },
            new AppUser { Id = "delta", UserName = "delta", CharacterName = "Delta" },
            new AppUser { Id = "echo", UserName = "echo", CharacterName = "Echo" });
        await db.SaveChangesAsync();

        // Balances that match the ledger: one opening row per member in the HNM pool.
        foreach (var (id, name, dkp) in new[]
                 {
                     ("alpha", "Alpha", 40d), ("beta", "Beta", 20d), ("gamma", "Gamma", 0d),
                     ("delta", "Delta", 10d), ("echo", "Echo", 0d),
                 })
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                LinkshellId = linkshell.Id, AppUserId = id, CharacterName = name, LinkshellDkp = dkp,
            });
            if (dkp != 0)
            {
                db.DkpLedgerEntries.Add(new DkpLedgerEntry
                {
                    LinkshellId = linkshell.Id, AppUserId = id, EntryType = "AuditAdjustment", Amount = dkp,
                    DkpPoolId = hnm.Id, EventType = "HNM", OccurredAt = _t0.AddDays(-7), CharacterName = name,
                });
            }
        }
        await db.SaveChangesAsync();
        return new Seeded(linkshell.Id, 0, main.Id, hnm.Id);
    }

    private async Task<int> AddBoardAsync(
        ApplicationDbContext db, int linkshellId, string monster, string? attendanceMode = null,
        bool repeats = true, int dayNumber = 3)
    {
        if (repeats)
        {
            db.HnmRecurringBoards.Add(new HnmRecurringBoard
            {
                LinkshellId = linkshellId, MonsterName = monster, Enabled = true, LeadHours = 2,
            });
        }
        var board = new Event
        {
            LinkshellId = linkshellId,
            EventName = monster,
            EventType = "HNM",
            EventLocation = "Dragon's Aery",
            AssignedMonsterName = monster,
            StartTime = _t0.AddHours(-3),
            CommencementStartTime = _t0.AddHours(-3),
            HnmWindowNumber = 4,
            DayNumber = dayNumber,
            WindowMethod = MonsterWindowMethods.WindowN,
            AttendanceMode = attendanceMode,
        };
        db.Events.Add(board);
        await db.SaveChangesAsync();
        return board.Id;
    }

    private static AppUserEvent Join(int boardId, string? appUserId, string name, string job, string sub) => new()
    {
        EventId = boardId, AppUserId = appUserId, CharacterName = name, JobName = job, SubJobName = sub,
    };

    // One person the addon scanned in a window. `Main` is set when the scan caught them on an alt.
    private sealed record Scan(string? AppUserId, string Name, AppUserEvent? Join, string? Main = null);

    // One posted window and who the addon scanned in it.
    private EventAttendanceWindow Window(
        int boardId, int sequence, int minutesAfterStart, Scan[] scans, bool close = false, bool kill = false,
        double? dkp = null, int? spawnWindow = null)
    {
        var postedAt = _t0.AddHours(-3).AddMinutes(minutesAfterStart);
        var window = new EventAttendanceWindow
        {
            EventId = boardId,
            SequenceNumber = sequence,
            PostedAt = postedAt,
            IsClosingWindow = close,
            IsKillWindow = kill,
            DkpAmount = dkp,
            SpawnWindow = spawnWindow,
            PostedBySource = "lsm-addon (lsm)",
            Label = kill ? "Kill" : null,
        };
        var alliance = 1;
        foreach (var scan in scans)
        {
            window.Attendees.Add(new AppUserEventWindow
            {
                AppUserId = scan.AppUserId,
                CharacterName = scan.Name,
                MainCharacterName = scan.Main,
                AppUserEvent = scan.Join,
                VerifiedAt = postedAt,
                VerifiedBy = "Alpha",
                AllianceNumber = alliance,
                AllianceKey = "ally-1",
                Zone = "Dragon's Aery",
            });
        }
        return window;
    }

    // A `/lsm now` an officer filed against the live camp as a misc post.
    private AttendanceSnapshot MiscPost(
        int linkshellId, int boardId, string name, int minutesAfterStart, double? dkp, string status, params string[] people)
    {
        var post = new AttendanceSnapshot
        {
            LinkshellId = linkshellId,
            LinkedEventId = boardId,
            Name = name,
            SlotKind = AttendanceSnapshotSlotKinds.Misc,
            CapturedAtUtc = _t0.AddHours(-3).AddMinutes(minutesAfterStart),
            CapturedByCharacterName = "Alpha",
            SnapshotStatus = status,
            DkpAmount = dkp,
            EntryCount = people.Length,
            PostSource = "lsm now",
        };
        foreach (var person in people)
        {
            post.Entries.Add(new AttendanceSnapshotEntry { CharacterName = person, Zone = "Dragon's Aery" });
        }
        return post;
    }

    private ClaimShieldCapture Tags(int linkshellId, int boardId, string monster, params (string Name, string? AppUserId)[] taggers)
    {
        var capture = new ClaimShieldCapture
        {
            LinkshellId = linkshellId,
            EventId = boardId,
            MonsterName = monster,
            Won = true,
            TotalPlayers = taggers.Length,
            CapturedAtUtc = _t0.AddMinutes(-10),
            CapturedByCharacterName = "Alpha",
            CapturedMessage = "Claim Shield: won",
            CreatedAtUtc = _t0.AddMinutes(-10),
        };
        foreach (var (name, appUserId) in taggers)
        {
            capture.Members.Add(new ClaimShieldCaptureMember
            {
                CharacterName = name, AppUserId = appUserId, Matched = appUserId is not null, ActionMessage = "hits",
            });
        }
        return capture;
    }

    private EventLootDetail Loot(
        int linkshellId, int boardId, string item, string winner, int dkp, int? poolId = null, bool alreadyCharged = false)
        => new()
        {
            LinkshellId = linkshellId,
            EventId = boardId,
            ItemName = item,
            ItemWinner = winner,
            WinningDkpSpent = dkp,
            DkpPoolId = poolId,
            DkpDebitedAt = alreadyCharged ? _t0.AddMinutes(-20) : null,
            ActualDeductedDkp = alreadyCharged ? dkp : null,
        };

    // A full Standard night: four windows (the open, one an officer re-priced, the ticked close,
    // and the Post Kill roster), the Claim Shield, misc posts, and four pieces of loot.
    private async Task<Seeded> SeedStandardCampAsync()
    {
        await using var db = _pg.CreateContext();
        var seeded = await SeedLinkshellAsync(db);
        var ls = seeded.LinkshellId;
        var boardId = await AddBoardAsync(db, ls, "Fafnir");

        var alpha = Join(boardId, "alpha", "Alpha", "WAR", "NIN");
        var beta = Join(boardId, "beta", "Betalt", "White Mage", "BLM");
        var gamma = Join(boardId, "gamma", "Gamma", "RDM", "WHM");
        var stranger = Join(boardId, null, "Stranger", "THF", "NIN");
        db.AppUserEvents.AddRange(alpha, beta, gamma, stranger);

        db.EventAttendanceWindows.AddRange(
            Window(boardId, 1, 10, spawnWindow: 1, scans: new[]
            {
                new Scan("alpha", "Alpha", alpha), new Scan("gamma", "Gamma", gamma), new Scan(null, "Stranger", stranger),
            }),
            Window(boardId, 2, 20, dkp: 1.25, spawnWindow: 2, scans: new[]
            {
                new Scan("alpha", "Alpha", alpha), new Scan("beta", "Betalt", beta, Main: "Beta"),
            }),
            Window(boardId, 3, 30, close: true, spawnWindow: 3, scans: new[]
            {
                new Scan("alpha", "Alpha", alpha), new Scan("beta", "Betalt", beta, Main: "Beta"),
                new Scan(null, "Stranger", stranger),
            }),
            Window(boardId, 4, 40, kill: true, spawnWindow: 4, scans: new[]
            {
                new Scan("alpha", "Alpha", alpha), new Scan("gamma", "Gamma", gamma),
            }));

        db.ClaimShieldCaptures.Add(Tags(ls, boardId, "Fafnir", ("Alpha", "alpha"), ("Delta", "delta"), ("Randomguy", null)));

        db.AttendanceSnapshots.AddRange(
            MiscPost(ls, boardId, "Runs to the zone", 5, dkp: null, AttendanceSnapshotStatuses.Active, "Beta", "Echo", "Beta"),
            MiscPost(ls, boardId, "Pulled the PH", 15, dkp: 2, AttendanceSnapshotStatuses.Active, "Alpha"),
            MiscPost(ls, boardId, "Duplicate post", 25, dkp: null, AttendanceSnapshotStatuses.Ignored, "Gamma"));

        db.EventLootDetails.AddRange(
            Loot(ls, boardId, "Ridill", "Alpha", 10),                                   // charged from the camp's pool
            Loot(ls, boardId, "Dragon Heart", "Betalt", 6, poolId: seeded.MainPoolId),  // won on an alt, pinned pool
            Loot(ls, boardId, "Fafnir Scale", "Nobody", 4),                             // not on the roster
            Loot(ls, boardId, "Wyrm Beard", "Delta", 3, alreadyCharged: true));         // charged before End Camp
        await db.SaveChangesAsync();

        return seeded with { BoardId = boardId };
    }

    // --------------------------------------------------------------------------------- tests ---

    // The main payout path: claimed and killed, every kind of window, every kind of member.
    [Fact]
    public async Task StandardCamp_EndedFromTheBoard_ThenPosted()
    {
        var seeded = await SeedStandardCampAsync();

        await EndCampFromTheBoardAsync(seeded.BoardId, claimed: true, killed: true, popWindow: 4);
        var afterEndCamp = await DumpAsync();

        await PostEveryPendingCardAsync();
        var afterPost = await DumpAsync();

        DatabaseStateDump.AssertMatchesSnapshot(
            nameof(StandardCamp_EndedFromTheBoard_ThenPosted),
            "## after End Camp\n" + afterEndCamp + "\n## after Post\n" + afterPost);
    }

    // The same night, but the mob was neither claimed nor killed: no claim or kill bonus anywhere.
    [Fact]
    public async Task StandardCamp_NotClaimedNotKilled()
    {
        var seeded = await SeedStandardCampAsync();

        await EndCampFromTheBoardAsync(seeded.BoardId, claimed: false, killed: false, popWindow: 3);
        var afterEndCamp = await DumpAsync();

        await PostEveryPendingCardAsync();
        var afterPost = await DumpAsync();

        DatabaseStateDump.AssertMatchesSnapshot(
            nameof(StandardCamp_NotClaimedNotKilled),
            "## after End Camp\n" + afterEndCamp + "\n## after Post\n" + afterPost);
    }

    // Ended from the addon (HandOffAndRecycleAsync), which works the claim and kill out from the
    // Claim Shield capture and the Post Kill roster instead of being told.
    [Fact]
    public async Task StandardCamp_EndedFromTheAddon_ThenPosted()
    {
        var seeded = await SeedStandardCampAsync();

        await EndCampFromTheAddonAsync(seeded.BoardId);
        var afterEndCamp = await DumpAsync();

        await PostEveryPendingCardAsync();
        var afterPost = await DumpAsync();

        DatabaseStateDump.AssertMatchesSnapshot(
            nameof(StandardCamp_EndedFromTheAddon_ThenPosted),
            "## after End Camp\n" + afterEndCamp + "\n## after Post\n" + afterPost);
    }

    // A Manual Check In ("Wd") linkshell: credit from each member's check-in range, one row per
    // member instead of per capture, and Hybrid loot (a percentage of the winner's pool balance).
    [Fact]
    public async Task ManualCheckInCamp_WithHybridLoot_ThenPosted()
    {
        int boardId;
        await using (var db = _pg.CreateContext())
        {
            var seeded = await SeedLinkshellAsync(db, HnmAttendanceModes.Wd, lootStructure: "Hybrid");
            var ls = seeded.LinkshellId;
            boardId = await AddBoardAsync(db, ls, "Fafnir", attendanceMode: HnmAttendanceModes.Wd);

            var alpha = Join(boardId, "alpha", "Alpha", "WAR", "NIN");
            alpha.WdArrivalWindow = 1;
            var beta = Join(boardId, "beta", "Betalt", "WHM", "BLM");
            beta.WdArrivalWindow = 2;
            beta.WdDepartureWindow = 3;
            var gamma = Join(boardId, "gamma", "Gamma", "RDM", "WHM");
            gamma.WdArrivalWindow = 1;
            var outsider = Join(boardId, null, "Outsider", "BRD", "WHM");
            outsider.WdArrivalWindow = 3;
            var neverCheckedIn = Join(boardId, "echo", "Echo", "SAM", "WAR");
            db.AppUserEvents.AddRange(alpha, beta, gamma, outsider, neverCheckedIn);

            db.EventAttendanceWindows.AddRange(
                Window(boardId, 1, 10, scans: new[] { new Scan("alpha", "Alpha", alpha), new Scan("gamma", "Gamma", gamma) }),
                Window(boardId, 2, 20, scans: new[] { new Scan("alpha", "Alpha", alpha), new Scan("beta", "Betalt", beta, Main: "Beta") }));
            db.ClaimShieldCaptures.Add(Tags(ls, boardId, "Fafnir", ("Alpha", "alpha"), ("Beta", "beta")));
            db.AttendanceSnapshots.Add(
                MiscPost(ls, boardId, "Runs to the zone", 5, dkp: null, AttendanceSnapshotStatuses.Active, "Echo"));
            db.EventLootDetails.AddRange(
                Loot(ls, boardId, "Ridill", "Alpha", 50),                                    // 50% of Alpha's HNM pool
                Loot(ls, boardId, "Dragon Heart", "Beta", 25, poolId: seeded.MainPoolId));   // 25% of Beta's Main pool
            await db.SaveChangesAsync();
        }

        await EndCampFromTheBoardAsync(boardId, claimed: true, killed: true, popWindow: 4);
        var afterEndCamp = await DumpAsync();

        await PostEveryPendingCardAsync();
        var afterPost = await DumpAsync();

        DatabaseStateDump.AssertMatchesSnapshot(
            nameof(ManualCheckInCamp_WithHybridLoot_ThenPosted),
            "## after End Camp\n" + afterEndCamp + "\n## after Post\n" + afterPost);
    }

    // An HQ kill rolls the board back to its base monster and day 1 when it is revived, and the
    // SAME board row then runs a second camp. The second End Camp must hand off only its own
    // windows, captures and loot -- nothing left over from the first.
    [Fact]
    public async Task HqKill_ThenTheRecycledBoardRunsASecondCamp()
    {
        int linkshellId, boardId;
        await using (var db = _pg.CreateContext())
        {
            var seeded = await SeedLinkshellAsync(db);
            linkshellId = seeded.LinkshellId;
            boardId = await AddBoardAsync(db, linkshellId, "Fafnir/Nidhogg", dayNumber: 5);

            var alpha = Join(boardId, "alpha", "Alpha", "WAR", "NIN");
            var beta = Join(boardId, "beta", "Betalt", "WHM", "BLM");
            db.AppUserEvents.AddRange(alpha, beta);
            db.EventAttendanceWindows.AddRange(
                Window(boardId, 1, 10, scans: new[] { new Scan("alpha", "Alpha", alpha), new Scan("beta", "Betalt", beta, Main: "Beta") }),
                Window(boardId, 2, 20, close: true, scans: new[] { new Scan("alpha", "Alpha", alpha) }));
            db.ClaimShieldCaptures.Add(Tags(linkshellId, boardId, "Nidhogg", ("Alpha", "alpha")));
            db.EventLootDetails.Add(Loot(linkshellId, boardId, "Nidhogg Scale", "Alpha", 5));
            await db.SaveChangesAsync();
        }

        // Camp 1: Nidhogg (the HQ) killed on day 5.
        await EndCampFromTheBoardAsync(boardId, claimed: true, killed: true, popWindow: 2, hq: true, dayNumber: 5);
        var afterFirstCamp = await DumpAsync();

        // The poller revives the parked board for the next pop, then the next camp goes live.
        await using (var db = _pg.CreateContext())
        {
            var board = await db.Events.SingleAsync(e => e.Id == boardId);
            var tod = await db.Tods.SingleAsync(t => t.Id == board.SourceTodId);
            var nextMonster = HnmDayCycle.WasHq(tod) ? HnmConfig.BaseMonsterName(board.AssignedMonsterName) ?? board.AssignedMonsterName : board.AssignedMonsterName;
            await HnmEventSeeder.ReviveForNewPopAsync(
                db, board, _t0.AddHours(-1), HnmDayCycle.NextDayForBoard(board, tod), nextMonster, tod.Id);
            await db.SaveChangesAsync();
            board.CommencementStartTime = _t0.AddHours(-1);
            board.HnmWindowNumber = 1;

            var gamma = Join(boardId, "gamma", "Gamma", "RDM", "WHM");
            var alpha = Join(boardId, "alpha", "Alpha", "WAR", "NIN");
            db.AppUserEvents.AddRange(gamma, alpha);
            db.EventAttendanceWindows.AddRange(
                Window(boardId, 1, 130, scans: new[] { new Scan("gamma", "Gamma", gamma), new Scan("alpha", "Alpha", alpha) }),
                Window(boardId, 2, 140, kill: true, scans: new[] { new Scan("gamma", "Gamma", gamma) }));
            db.ClaimShieldCaptures.Add(Tags(linkshellId, boardId, "Fafnir", ("Gamma", "gamma")));
            db.EventLootDetails.Add(Loot(linkshellId, boardId, "Fafnir Scale", "Gamma", 2));
            await db.SaveChangesAsync();
        }

        // Camp 2: ended from the addon this time.
        await EndCampFromTheAddonAsync(boardId);
        var afterSecondCamp = await DumpAsync();

        await PostEveryPendingCardAsync();
        var afterPost = await DumpAsync();

        DatabaseStateDump.AssertMatchesSnapshot(
            nameof(HqKill_ThenTheRecycledBoardRunsASecondCamp),
            "## after camp 1\n" + afterFirstCamp
            + "\n## after camp 2\n" + afterSecondCamp
            + "\n## after Post\n" + afterPost);
    }

    // A camp whose only attendance is misc posts still becomes a review card.
    [Fact]
    public async Task CampWithOnlyMiscPosts_ThenPosted()
    {
        int boardId;
        await using (var db = _pg.CreateContext())
        {
            var seeded = await SeedLinkshellAsync(db);
            boardId = await AddBoardAsync(db, seeded.LinkshellId, "Fafnir");
            db.AttendanceSnapshots.AddRange(
                MiscPost(seeded.LinkshellId, boardId, "Runs to the zone", 5, dkp: null, AttendanceSnapshotStatuses.Active, "Alpha", "Beta"),
                MiscPost(seeded.LinkshellId, boardId, "Holding the spot", 50, dkp: 1.5, AttendanceSnapshotStatuses.Pending, "Echo"));
            await db.SaveChangesAsync();
        }

        await EndCampFromTheBoardAsync(boardId, claimed: false, killed: false, popWindow: 2);
        var afterEndCamp = await DumpAsync();

        await PostEveryPendingCardAsync();
        var afterPost = await DumpAsync();

        DatabaseStateDump.AssertMatchesSnapshot(
            nameof(CampWithOnlyMiscPosts_ThenPosted),
            "## after End Camp\n" + afterEndCamp + "\n## after Post\n" + afterPost);
    }

    // Nobody on the roster: no review card, but the loot is still charged and moved off the board,
    // and the Claim Shield capture and an ignored post are detached so the next pop cannot pick
    // them up. The board does not repeat, so End Event removes it.
    [Fact]
    public async Task EmptyCampOnABoardThatDoesNotRepeat()
    {
        int boardId;
        await using (var db = _pg.CreateContext())
        {
            var seeded = await SeedLinkshellAsync(db);
            var ls = seeded.LinkshellId;
            boardId = await AddBoardAsync(db, ls, "Fafnir", repeats: false);
            db.ClaimShieldCaptures.Add(Tags(ls, boardId, "Fafnir", ("Randomguy", null)));
            db.AttendanceSnapshots.Add(
                MiscPost(ls, boardId, "Duplicate post", 25, dkp: null, AttendanceSnapshotStatuses.Ignored, "Alpha"));
            db.EventLootDetails.AddRange(
                Loot(ls, boardId, "Ridill", "Alpha", 10),
                Loot(ls, boardId, "Fafnir Scale", "Nobody", 4));
            await db.SaveChangesAsync();
        }

        await EndCampFromTheAddonAsync(boardId);

        await using (var check = _pg.CreateContext())
        {
            Assert.Empty(await check.WindowEvents.ToListAsync());
            Assert.Empty(await check.Events.ToListAsync());
        }
        DatabaseStateDump.AssertMatchesSnapshot(
            nameof(EmptyCampOnABoardThatDoesNotRepeat), "## after End Camp\n" + await DumpAsync());
    }
}
