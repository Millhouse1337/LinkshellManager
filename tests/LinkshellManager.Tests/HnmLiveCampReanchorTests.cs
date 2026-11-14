using System;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Moving a LIVE camp's start time from the edit form.
//
// The window grid counts from WindowAnchorAt, stamped once when the camp went live, and the
// advancer only ever moves the counter up. So an edit to the start changed the "Started" line and
// nothing else: a camp that went live an hour early kept reading "Window 7 of 7 / final window"
// after being moved to the real pop. ReanchorToStart moves the grid with the start.
public class HnmLiveCampReanchorTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 14, 45, 0, DateTimeKind.Utc);

    // A 7 x 10-minute king/dragon camp that went live at 13:39 and has run out its windows.
    private static Event ExpiredCamp() => new()
    {
        Id = 1,
        LinkshellId = 12,
        EventName = "Turtle",
        EventType = "HNM",
        AssignedMonsterName = "Adamantoise/Aspidochelone",
        SpawnWindowCount = 7,
        SpawnWindowMinutes = 10,
        StartTime = new DateTime(2026, 11, 4, 13, 39, 21, DateTimeKind.Utc),
        CommencementStartTime = new DateTime(2026, 11, 4, 13, 39, 21, DateTimeKind.Utc),
        WindowAnchorAt = new DateTime(2026, 11, 4, 13, 39, 21, DateTimeKind.Utc),
        HnmWindowNumber = 7,
        NextWindowAt = null,
        HnmClearedWindow = 7,
    };

    [Fact]
    public void MovingTheStartLater_PutsTheCounterBackOnTheRightWindow()
    {
        var ev = ExpiredCamp();
        var realPop = new DateTime(2026, 11, 4, 14, 39, 21, DateTimeKind.Utc); // 6 min before Now

        HnmWindowAdvanceBackgroundService.ReanchorToStart(ev, realPop, Now);

        Assert.Equal(realPop, ev.WindowAnchorAt);
        Assert.Equal(1, ev.HnmWindowNumber);
        Assert.Equal(realPop.AddMinutes(10), ev.NextWindowAt);
        // Board now reads Window 2 (awaited); marked settled so the edit itself wipes nothing.
        Assert.Equal(2, HnmCampRules.FocusWindow(ev));
        Assert.Equal(2, ev.HnmClearedWindow);
    }

    [Fact]
    public void MovingTheStartIntoTheFuture_WaitsOnWindowOne()
    {
        var ev = ExpiredCamp();
        var later = Now.AddMinutes(20);

        HnmWindowAdvanceBackgroundService.ReanchorToStart(ev, later, Now);

        Assert.Equal(1, ev.HnmWindowNumber);
        Assert.Equal(later.AddMinutes(10), ev.NextWindowAt);
    }

    [Fact]
    public void MovingTheStartEarlier_CanMoveTheCounterUp()
    {
        var ev = ExpiredCamp();
        ev.HnmWindowNumber = 1;
        ev.NextWindowAt = Now.AddMinutes(5);
        var earlier = Now.AddMinutes(-35); // windows 1-3 opened, window 4 open now

        HnmWindowAdvanceBackgroundService.ReanchorToStart(ev, earlier, Now);

        Assert.Equal(4, ev.HnmWindowNumber);
        Assert.Equal(earlier.AddMinutes(40), ev.NextWindowAt);
    }

    [Fact]
    public void ACampWithNoTimedCadence_IsLeftAlone()
    {
        var ev = ExpiredCamp();
        ev.AssignedMonsterName = "Some Farm NM";
        ev.SpawnWindowCount = null;
        ev.SpawnWindowMinutes = null;
        var anchorBefore = ev.WindowAnchorAt;

        HnmWindowAdvanceBackgroundService.ReanchorToStart(ev, Now, Now);

        Assert.Equal(anchorBefore, ev.WindowAnchorAt);
        Assert.Equal(7, ev.HnmWindowNumber);
    }
}
