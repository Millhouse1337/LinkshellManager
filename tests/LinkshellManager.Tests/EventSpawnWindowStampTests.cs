using System;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// A camp CAPTURES its spawn grid when it is created (Event.SpawnWindowCount / SpawnWindowMinutes)
// rather than reading the linkshell's monster setup live.
//
// That is what makes per-linkshell window counts safe: a live board can't jump from "Window 4 of
// 25" to "of 7" because someone edited a table mid-camp, and every attendance snapshot already
// numbered against the old grid keeps meaning what it meant. It is also why the deploy is a no-op —
// nothing back-fills the stamp, so every camp in flight falls through to HnmConfig exactly as before.
public class EventSpawnWindowStampTests
{
    private static readonly DateTime CampStart = new(2026, 8, 18, 20, 0, 0, DateTimeKind.Utc);

    private static Event Camp(
        string monster = "Behemoth/King Behemoth",
        int? spawnWindowCount = null,
        int? spawnWindowMinutes = null,
        int? windowCountOverride = null) => new()
    {
        Id = 1,
        EventName = $"{monster} D1",
        EventType = "HNM",
        AssignedMonsterName = monster,
        StartTime = CampStart,
        CommencementStartTime = CampStart,
        WindowAnchorAt = CampStart,
        HnmWindowNumber = 1,
        SpawnWindowCount = spawnWindowCount,
        SpawnWindowMinutes = spawnWindowMinutes,
        WindowCountOverride = windowCountOverride,
    };

    // The deploy-safety case: an unstamped camp is byte-identical to how it behaved before the
    // columns existed.
    [Fact]
    public void UnstampedCamp_UsesTheBuiltInGrid()
    {
        var camp = Camp();
        Assert.Equal(7, DiscordEventMessageBuilder.EffectiveWindowCount(camp));
        Assert.Equal(10, DiscordEventMessageBuilder.EffectiveWindowMinutes(camp));
    }

    [Fact]
    public void StampedCamp_UsesItsOwnGrid()
    {
        var camp = Camp(spawnWindowCount: 12, spawnWindowMinutes: 30);
        Assert.Equal(12, DiscordEventMessageBuilder.EffectiveWindowCount(camp));
        Assert.Equal(30, DiscordEventMessageBuilder.EffectiveWindowMinutes(camp));
    }

    // The stamp wins over the monster's built-in cadence — that is the whole point of it — but it
    // is still clamped to the ceiling every downstream reader assumes.
    [Fact]
    public void StampedCamp_IsStillClampedToMaxWindow()
    {
        var camp = Camp(spawnWindowCount: 999, spawnWindowMinutes: 30);
        Assert.Equal(HnmConfig.MaxWindow, DiscordEventMessageBuilder.EffectiveWindowCount(camp));
    }

    // WindowCountOverride == 1 means "this camp is NOT windowed, pay it by accumulated duration",
    // which EventBreakPolicy reads to decide the camp keeps its Break Room. It has to short-circuit
    // BEFORE the stamp, or a stamped camp would flip onto the windowed payout path and strand its
    // members with no way to stop the clock.
    [Fact]
    public void OverrideOfOne_StillShortCircuits_EvenWithAStamp()
    {
        var camp = Camp(spawnWindowCount: 12, spawnWindowMinutes: 30, windowCountOverride: 1);
        Assert.Equal(1, DiscordEventMessageBuilder.EffectiveWindowCount(camp));
    }

    // An addon-made camp stores its POST count in WindowCountOverride. The stamp moves only the
    // spawn count, so the two numbers stay independent — the regression that comment warns about.
    [Fact]
    public void AddonPostCount_IsUnaffectedByTheStamp()
    {
        var camp = Camp(spawnWindowCount: 12, spawnWindowMinutes: 30, windowCountOverride: 2);
        camp.CreationSource = "Addon";

        Assert.Equal(12, DiscordEventMessageBuilder.EffectiveWindowCount(camp));
        // AttendancePostCount comes off the monster's tier for a curated HNM, not off the stamp,
        // so no camp gets its windows re-labelled Open/Close (or stops being).
        Assert.Equal(
            HnmConfig.GetWindowCount(camp.AssignedMonsterName),
            DiscordEventMessageBuilder.AttendancePostCount(camp));
    }

    // A monster with no BUILT-IN cadence that a linkshell gave one must light up the window UI, or
    // the configured grid would be stored and then ignored.
    [Fact]
    public void MonsterWithNoBuiltInCadence_UsesWindowsOnceStamped()
    {
        var unstamped = Camp("Serket");
        Assert.False(DiscordEventMessageBuilder.UsesWindows(unstamped));

        var stamped = Camp("Serket", spawnWindowCount: 6, spawnWindowMinutes: 20);
        Assert.True(DiscordEventMessageBuilder.UsesWindows(stamped));
        Assert.Equal(6, DiscordEventMessageBuilder.EffectiveWindowCount(stamped));
        Assert.Equal(20, DiscordEventMessageBuilder.EffectiveWindowMinutes(stamped));
    }

    // A grid-less monster must NOT be handed a cadence just because it carries a ToD check interval,
    // or the window advancer would start marching a board nobody meant to be windowed.
    [Fact]
    public void GridlessMonster_StaysManual()
    {
        var camp = Camp("Serket");
        Assert.Equal(0, DiscordEventMessageBuilder.EffectiveWindowMinutes(camp));
    }

    // The merge window scales to the window length a camp ACTUALLY runs, so a configured cadence
    // gets a sane answer instead of falling off the end of the old hardcoded 25/7 switch.
    [Theory]
    [InlineData(60, 5)]
    [InlineData(90, 5)]
    [InlineData(30, 3)]
    [InlineData(10, 3)]
    [InlineData(0, 3)]
    public void SnapshotMergeWindow_ScalesToTheCampsCadence(int cadenceMinutes, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), HnmConfig.SnapshotMergeWindow(cadenceMinutes));

    // ...and the name-based overload delegates to it, so the built-in monsters keep the exact
    // answers they had before.
    [Theory]
    [InlineData("Tiamat", 5)]
    [InlineData("Fafnir/Nidhogg", 3)]
    [InlineData("Serket", 3)]
    public void SnapshotMergeWindow_NameOverload_IsUnchanged(string monster, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), HnmConfig.SnapshotMergeWindow(monster));

    // The WindowEvent grid stamp does the same job for attendance snapshots: unstamped rows keep
    // their built-in numbering, so no snapshot in flight at deploy is ever re-labelled.
    [Fact]
    public void UnstampedWindowEvent_UsesTheBuiltInGrid()
    {
        var windowEvent = new WindowEvent
        {
            Name = "Behemoth/King Behemoth",
            WindowAnchorAtUtc = CampStart,
            FirstCapturedAtUtc = CampStart,
        };

        Assert.Equal(10, WindowEventWindowGrid.Minutes(windowEvent));
        Assert.Equal(7, WindowEventWindowGrid.WindowCount(windowEvent));
        Assert.Equal(3, WindowEventWindowGrid.SnapshotWindowNumber(windowEvent, CampStart.AddMinutes(25)));
    }

    [Fact]
    public void StampedWindowEvent_NumbersAgainstItsOwnGrid()
    {
        var windowEvent = new WindowEvent
        {
            Name = "Behemoth/King Behemoth",
            WindowAnchorAtUtc = CampStart,
            FirstCapturedAtUtc = CampStart,
            WindowCount = 6,
            WindowMinutes = 20,
        };

        Assert.Equal(20, WindowEventWindowGrid.Minutes(windowEvent));
        Assert.Equal(6, WindowEventWindowGrid.WindowCount(windowEvent));
        // 25 minutes in is window 2 on a 20-minute grid, not window 3 on a 10-minute one.
        Assert.Equal(2, WindowEventWindowGrid.SnapshotWindowNumber(windowEvent, CampStart.AddMinutes(25)));
        // ...and it can never run past its own count.
        Assert.Equal(6, WindowEventWindowGrid.SnapshotWindowNumber(windowEvent, CampStart.AddHours(9)));
    }

    // A camp with no grid at all reports no window number — which is different from window 1: the
    // UI shows no window tag rather than claiming everything happened in the first one.
    [Fact]
    public void GridlessWindowEvent_HasNoWindowNumber()
    {
        var windowEvent = new WindowEvent
        {
            Name = "Serket",
            WindowAnchorAtUtc = CampStart,
            FirstCapturedAtUtc = CampStart,
        };

        Assert.Null(WindowEventWindowGrid.SnapshotWindowNumber(windowEvent, CampStart.AddMinutes(25)));
    }
}
