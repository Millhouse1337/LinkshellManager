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

// Ending a camp with NO Time of Death must record no time and no repop.
//
// This used to stamp DateTime.UtcNow: an officer who ended the board after the last spawn window
// closed — nothing popped, or another linkshell took it — got a full ToD row whose "Time of Death"
// was really just the moment they clicked End Camp, plus a repop derived from it. The tracker then
// showed a confident timestamp for a pop nobody witnessed, and the whole day cycle drifted later
// with every miss. Now the absence is preserved and every surface renders it as "Not entered".
public class HnmCampPopServiceTests
{
    private const int LinkshellId = 7;
    private const int EventId = 100;

    private static readonly DateTime Now = new(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static HnmCampPopService NewService(ApplicationDbContext db)
    {
        var handoff = new HnmCampReviewHandoffService(
            db,
            new WdCampFinalizer(db, NullLogger<WdCampFinalizer>.Instance),
            new HnmStandardCampFinalizer(db, NullLogger<HnmStandardCampFinalizer>.Instance),
            NullLogger<HnmCampReviewHandoffService>.Instance);
        return new HnmCampPopService(db, handoff, NullLogger<HnmCampPopService>.Instance);
    }

    /// <summary>A live Standard-mode HNM camp on its 6th window, scheduled to have popped an hour ago.</summary>
    private static Event LiveCamp() => new()
    {
        Id = EventId,
        LinkshellId = LinkshellId,
        EventName = "Adamantoise/Aspidochelone Day 5",
        EventType = "HNM",
        AssignedMonsterName = "Adamantoise/Aspidochelone",
        DayNumber = 5,
        StartTime = Now.AddHours(-1),
        CommencementStartTime = Now.AddHours(-1),
        EndTime = null,
        HnmWindowNumber = 6,
        WindowAnchorAt = Now.AddHours(-1),
        NextWindowAt = Now.AddMinutes(-50),
    };

    private static async Task<ApplicationDbContext> SeededAsync(Event camp)
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell
        {
            Id = LinkshellId,
            LinkshellName = "Test",
            EnableHnmSection = true,
        });
        db.Events.Add(camp);
        await db.SaveChangesAsync();
        return db;
    }

    private static HnmCampPopService.Request PopRequest(DateTime? todTimeUtc) => new(
        EventId: EventId,
        TodTimeUtc: todTimeUtc,
        Cooldown: null,
        Interval: null,
        DayNumber: null,
        Claimed: false,
        Killed: false,
        PopWindow: null);

    [Fact]
    public async Task PopAsync_NoTod_LeavesTimeAndRepopUnrecorded()
    {
        var camp = LiveCamp();
        using var db = await SeededAsync(camp);

        var result = await NewService(db).PopAsync(PopRequest(null), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.RepopTimeUtc);

        var tod = await db.Tods.SingleAsync();
        Assert.Null(tod.Time);
        Assert.Null(tod.RepopTime);
        // The row still records everything that IS known about the camp, so the tracker can show
        // "Adamantoise/Aspidochelone · Day 5 · Unclaimed · Not entered" rather than dropping it.
        Assert.Equal("Adamantoise/Aspidochelone", tod.MonsterName);
        Assert.Equal(5, tod.DayNumber);
        Assert.False(tod.Claim);
        // TimeStamp is what every "latest ToD per monster" sort falls back to when Time is null.
        Assert.NotNull(tod.TimeStamp);
    }

    [Fact]
    public async Task PopAsync_NoTod_DoesNotRePointTheBoardOrScheduleARePost()
    {
        var camp = LiveCamp();
        var originalStart = camp.StartTime;
        using var db = await SeededAsync(camp);
        // Repeat-on-ToD is on for this monster, so a real ToD WOULD have scheduled a re-post.
        db.HnmRecurringBoards.Add(new HnmRecurringBoard
        {
            LinkshellId = LinkshellId,
            MonsterName = "Adamantoise/Aspidochelone",
            Enabled = true,
            LeadHours = 2,
        });
        await db.SaveChangesAsync();

        await NewService(db).PopAsync(PopRequest(null), CancellationToken.None);

        var saved = await db.Events.SingleAsync(e => e.Id == EventId);
        // No ToD means no predicted repop, so there is nothing honest to re-point to and nothing
        // to count a re-post lead back from. The board just closes; an officer posts the next one.
        Assert.Equal(originalStart, saved.StartTime);
        Assert.Null(saved.HnmRepostAt);
    }

    [Fact]
    public async Task PopAsync_NoTod_StillClosesTheCampAndRecordsThePopWindow()
    {
        var camp = LiveCamp();
        using var db = await SeededAsync(camp);

        await NewService(db).PopAsync(PopRequest(null), CancellationToken.None);

        var saved = await db.Events.SingleAsync(e => e.Id == EventId);
        // Everything except the ToD itself behaves exactly as before: the camp is over, the roster
        // is torn down, and the window it ended on is recorded so credit still caps correctly.
        Assert.NotNull(saved.HnmDefeatedAt);
        Assert.Null(saved.CommencementStartTime);
        Assert.Null(saved.NextWindowAt);

        var tod = await db.Tods.SingleAsync();
        Assert.NotNull(tod.PopWindow);
    }

    [Fact]
    public async Task PopAsync_WithTod_StillRecordsTimeAndDerivedRepop()
    {
        var camp = LiveCamp();
        using var db = await SeededAsync(camp);
        var observedTod = Now.AddMinutes(-15);

        var result = await NewService(db).PopAsync(PopRequest(observedTod), CancellationToken.None);

        Assert.True(result.Success);

        var tod = await db.Tods.SingleAsync();
        Assert.Equal(observedTod, tod.Time);
        // Adamantoise/Aspidochelone's default cooldown is 22 hours.
        Assert.Equal(observedTod.AddHours(22), tod.RepopTime);

        var saved = await db.Events.SingleAsync(e => e.Id == EventId);
        Assert.Equal(observedTod.AddHours(22), saved.StartTime);
    }
}
