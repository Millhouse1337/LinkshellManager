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

// Posting a ToD from the addon has to RECYCLE that monster's queued event for the new pop, not stack
// a second one beside it — otherwise every kill leaves another row in Queued Events and the old camps
// pile up until somebody ends them by hand.
//
// The exception is a camp that is already live: it has attendance and DKP accruing against it, and
// reviving it would wipe a night people are still being paid for. That guard is the difference
// between recycling a row and destroying a payout, so it gets its own test.
public class HnmAutoEventReviveTests
{
    private const int LinkshellId = 7;

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static readonly DateTime Now = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

    private static HnmAutoEventService NewService(ApplicationDbContext db) =>
        new(db, NullLogger<HnmAutoEventService>.Instance);

    private static Linkshell EnabledLinkshell() => new()
    {
        Id = LinkshellId,
        LinkshellName = "Test",
        EnableHnmSection = true,
    };

    /// <summary>A posted ToD whose repop is <paramref name="repopInHours"/> from now.</summary>
    private static Tod NewTod(int id, string monster, int? day = 1, bool hq = false, double repopInHours = 22)
        => new()
        {
            Id = id,
            LinkshellId = LinkshellId,
            MonsterName = monster,
            DayNumber = day,
            Hq = hq,
            Time = Now,
            RepopTime = Now.AddHours(repopInHours),
        };

    /// <summary>The previous pop's camp: assigned to <paramref name="monster"/>, start long past.</summary>
    private static Event PreviousCamp(string monster, string name) => new()
    {
        Id = 100,
        LinkshellId = LinkshellId,
        EventName = name,
        EventType = "HNM",
        AssignedMonsterName = monster,
        DayNumber = 1,
        StartTime = Now.AddDays(-1),
        CommencementStartTime = null,   // queued
        EndTime = null,
        HnmWindowNumber = 5,
        WindowAnchorAt = Now.AddDays(-1),
        NextWindowAt = Now.AddDays(-1).AddMinutes(10),
    };

    private static async Task<ApplicationDbContext> SeededAsync(params object[] rows)
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(EnabledLinkshell());
        foreach (var row in rows)
        {
            db.Add(row);
        }
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task NewTod_RevivesTheQueuedEvent_RatherThanCreatingASecond()
    {
        using var db = await SeededAsync(
            PreviousCamp("Fafnir", "Fafnir D1"),
            NewTod(1, "Fafnir"));

        var id = await NewService(db).CreateAutoEventForTodAsync(1, CancellationToken.None);

        Assert.Equal(100, id);
        Assert.Single(db.Events);
    }

    // The money test. A live camp is mid-payout; the new pop queues alongside it and ending the old
    // one stays a deliberate officer action.
    [Fact]
    public async Task NewTod_LeavesALiveCampAlone_AndQueuesTheNewPopSeparately()
    {
        var live = PreviousCamp("Fafnir", "Fafnir D1");
        live.CommencementStartTime = Now.AddHours(-2);   // started, never ended

        using var db = await SeededAsync(live, NewTod(1, "Fafnir"));

        var id = await NewService(db).CreateAutoEventForTodAsync(1, CancellationToken.None);

        Assert.NotEqual(100, id);
        Assert.Equal(2, db.Events.Count());

        var untouched = db.Events.Single(e => e.Id == 100);
        Assert.Equal(Now.AddHours(-2), untouched.CommencementStartTime);
        Assert.Equal(5, untouched.HnmWindowNumber);
        Assert.Equal(Now.AddDays(-1), untouched.StartTime);
    }

    [Fact]
    public async Task Revive_ResetsTheClockAndTheWindowCounter()
    {
        using var db = await SeededAsync(
            PreviousCamp("Fafnir", "Fafnir D1"),
            NewTod(1, "Fafnir"));

        await NewService(db).CreateAutoEventForTodAsync(1, CancellationToken.None);

        var revived = db.Events.Single();
        Assert.Equal(Now.AddHours(22), revived.StartTime);
        Assert.Equal(1, revived.HnmWindowNumber);
        Assert.Null(revived.CommencementStartTime);
        Assert.Null(revived.WindowAnchorAt);
        Assert.Null(revived.NextWindowAt);
        Assert.Null(revived.HnmDefeatedAt);
    }

    // Every Manual Check In surface gates on WdFinalizedAt being null. A recycled camp that keeps the previous
    // pop's sentinels reads as "already processed" forever and Manual Check In silently disappears.
    [Fact]
    public async Task Revive_ClearsTheManualCheckInSentinels()
    {
        var camp = PreviousCamp("Fafnir", "Fafnir D1");
        camp.AttendanceMode = HnmAttendanceModes.Wd;
        camp.WdFinalizedAt = Now.AddHours(-1);
        camp.WdAwaitingProcessingSince = Now.AddHours(-2);
        camp.WdPopWindow = 6;
        camp.WdClaimed = true;
        camp.WdKilled = true;

        using var db = await SeededAsync(camp, NewTod(1, "Fafnir"));

        await NewService(db).CreateAutoEventForTodAsync(1, CancellationToken.None);

        var revived = db.Events.Single();
        Assert.Null(revived.WdFinalizedAt);
        Assert.Null(revived.WdAwaitingProcessingSince);
        Assert.Null(revived.WdPopWindow);
        Assert.False(revived.WdClaimed);
        Assert.False(revived.WdKilled);
    }

    // The revived board is for the NEXT pop, so it carries the next day of the cycle.
    [Fact]
    public async Task Revive_AdvancesTheDayCycle()
    {
        var camp = PreviousCamp("Fafnir", "Fafnir D1");
        camp.DayNumber = 1;

        using var db = await SeededAsync(camp, NewTod(1, "Fafnir", day: 1));

        await NewService(db).CreateAutoEventForTodAsync(1, CancellationToken.None);

        Assert.Equal(2, db.Events.Single().DayNumber);
    }

    // Killing the HQ half spends the cycle: the next spawn is the NQ, back at day 1.
    [Fact]
    public async Task Revive_AfterAnHqKill_SwapsBackToTheNqAtDayOne()
    {
        using var db = await SeededAsync(
            PreviousCamp("Nidhogg", "Nidhogg D4"),
            NewTod(1, "Nidhogg", day: 4, hq: true));

        await NewService(db).CreateAutoEventForTodAsync(1, CancellationToken.None);

        var revived = db.Events.Single();
        Assert.Equal("Fafnir", revived.AssignedMonsterName);
        Assert.Equal(1, revived.DayNumber);
    }

    // The merge-pair case, and the reason matching is on AssignedMonsterName rather than EventName:
    // a Fafnir ToD has to find the event assigned to Nidhogg. Matching on the name would miss —
    // the "D<n>" suffix changes every pop.
    [Fact]
    public async Task Revive_MatchesAcrossTheMergePair()
    {
        using var db = await SeededAsync(
            PreviousCamp("Nidhogg", "Nidhogg D4"),
            NewTod(1, "Fafnir", day: 1));

        var id = await NewService(db).CreateAutoEventForTodAsync(1, CancellationToken.None);

        Assert.Equal(100, id);
        Assert.Single(db.Events);
    }

    // Stamping the source ToD is what makes the two revive callers coexist: the recurring board's
    // idempotency check matches on SourceTodId, so without this it posts a second board for the
    // same pop.
    [Fact]
    public async Task Revive_StampsSourceTodId()
    {
        using var db = await SeededAsync(
            PreviousCamp("Fafnir", "Fafnir D1"),
            NewTod(42, "Fafnir"));

        await NewService(db).CreateAutoEventForTodAsync(42, CancellationToken.None);

        Assert.Equal(42, db.Events.Single().SourceTodId);
    }

    // A different monster's camp must not be recycled by this ToD.
    [Fact]
    public async Task Revive_IgnoresAnotherMonstersCamp()
    {
        using var db = await SeededAsync(
            PreviousCamp("Behemoth", "Behemoth D1"),
            NewTod(1, "Fafnir"));

        var id = await NewService(db).CreateAutoEventForTodAsync(1, CancellationToken.None);

        Assert.NotEqual(100, id);
        Assert.Equal(2, db.Events.Count());
        Assert.Equal(Now.AddDays(-1), db.Events.Single(e => e.Id == 100).StartTime);
    }

    // A closed camp is history; recycling it would rewrite a finished night.
    [Fact]
    public async Task Revive_IgnoresAnEndedCamp()
    {
        var ended = PreviousCamp("Fafnir", "Fafnir D1");
        ended.EndTime = Now.AddHours(-3);

        using var db = await SeededAsync(ended, NewTod(1, "Fafnir"));

        var id = await NewService(db).CreateAutoEventForTodAsync(1, CancellationToken.None);

        Assert.NotEqual(100, id);
        Assert.Equal(Now.AddHours(-3), db.Events.Single(e => e.Id == 100).EndTime);
    }

    // Another linkshell's camp for the same monster is not ours to touch.
    [Fact]
    public async Task Revive_IgnoresAnotherLinkshellsCamp()
    {
        var theirs = PreviousCamp("Fafnir", "Fafnir D1");
        theirs.LinkshellId = 999;

        using var db = await SeededAsync(theirs, NewTod(1, "Fafnir"));

        var id = await NewService(db).CreateAutoEventForTodAsync(1, CancellationToken.None);

        Assert.NotEqual(100, id);
        Assert.Equal(999, db.Events.Single(e => e.Id == 100).LinkshellId);
    }

    // Unchanged behaviour: with nothing to recycle, a fresh queued event is created for the repop.
    [Fact]
    public async Task NoExistingCamp_StillCreatesTheEventAtTheRepop()
    {
        using var db = await SeededAsync(NewTod(1, "Fafnir"));

        var id = await NewService(db).CreateAutoEventForTodAsync(1, CancellationToken.None);

        var created = db.Events.Single();
        Assert.Equal(created.Id, id);
        Assert.Equal(Now.AddHours(22), created.StartTime);
        Assert.Null(created.CommencementStartTime);   // lands in Queued, not live
        Assert.Equal(1, created.SourceTodId);
    }

    // Unchanged behaviour: an event already sitting at this repop is relinked, never duplicated.
    //
    // The name is bare "Fafnir", not "Fafnir D2": HnmDayCycles is keyed on the STRONGER halves
    // (Nidhogg / King Behemoth / Aspidochelone), so ComposeEventName only appends a day suffix for
    // those. An NQ camp carries a DayNumber but no suffix in its name.
    [Fact]
    public async Task AnEventAlreadyAtThisRepop_IsRelinkedNotRevived()
    {
        var alreadyThere = PreviousCamp("Fafnir", "Fafnir");
        alreadyThere.StartTime = Now.AddHours(22).AddMinutes(3);   // inside the ±10 min window

        using var db = await SeededAsync(alreadyThere, NewTod(1, "Fafnir"));

        var id = await NewService(db).CreateAutoEventForTodAsync(1, CancellationToken.None);

        Assert.Equal(100, id);
        Assert.Single(db.Events);
        // Relink only — the existing row keeps its own start rather than being reset.
        Assert.Equal(Now.AddHours(22).AddMinutes(3), db.Events.Single().StartTime);
        Assert.Equal(1, db.Events.Single().SourceTodId);
    }
}
