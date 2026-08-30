using System.Collections.Generic;
using System.Linq;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.ViewModels;
using Xunit;

namespace LinkshellManager.Tests;

// The website's Attendance Windows card renders a tab per window the camp has SAT THROUGH, not
// per window somebody was around to post.
//
// The bug: the card looped over the posted rows and nothing else. A 25-window wyrm where only
// windows 5-7 landed showed three tabs and read as a camp that had run three windows. Windows 1-4
// happened; nobody recorded them. That gap is exactly what tells an officer to go back and file
// one, and dropping it made a half-covered camp look complete.
//
// EventController.BuildAttendanceWindowTabs is a MIRROR of the Activity's attendanceWindowTabs()
// (discord-activity/src/app/home/tabs/events-tab.component.ts). These tests pin the three claims
// that make the two agree: the synthesis, the counter it's bounded by, and the two-post exemption.
public class AttendanceWindowTabsTests
{
    private static EventAttendanceWindowViewModel Posted(int sequence, int attendees = 1) =>
        new()
        {
            Id = sequence * 10,
            SequenceNumber = sequence,
            Attendees = Enumerable.Range(0, attendees)
                .Select(i => new AttendanceWindowAttendeeViewModel { Id = sequence * 100 + i })
                .ToList(),
        };

    // The reported case: a wyrm sitting on window 7 with only 5, 6 and 7 posted.
    [Fact]
    public void ReachedButUnpostedWindows_StillGetTabs()
    {
        var tabs = EventController.BuildAttendanceWindowTabs(
            new List<EventAttendanceWindowViewModel> { Posted(5), Posted(6), Posted(7) },
            postCount: 25,
            hnmWindowNumber: 7);

        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7 }, tabs.Select(t => t.SequenceNumber));
        Assert.Equal(new[] { 1, 2, 3, 4 }, tabs.Where(t => t.Window is null).Select(t => t.SequenceNumber));
        Assert.Equal("Window 3", tabs[2].Label);
        Assert.Equal(0, tabs[2].AttendeeCount);
    }

    // The bound is the OPENED counter, not the spawn count — a camp on window 7 of 25 must not
    // sprout eighteen tabs for windows it hasn't reached yet.
    [Fact]
    public void UnreachedWindows_AreNotInvented()
    {
        var tabs = EventController.BuildAttendanceWindowTabs(
            new List<EventAttendanceWindowViewModel>(),
            postCount: 25,
            hnmWindowNumber: 3);

        Assert.Equal(new[] { 1, 2, 3 }, tabs.Select(t => t.SequenceNumber));
    }

    // A 2-post king/dragon names its windows Open / Close while its counter walks the seven SPAWN
    // windows underneath. Synthesizing 1..opened there would invent five windows that camp can
    // never post — the phantom-tab trap the Activity's version is written around.
    [Fact]
    public void TwoPostCamp_SynthesizesNothing()
    {
        var tabs = EventController.BuildAttendanceWindowTabs(
            new List<EventAttendanceWindowViewModel> { Posted(1) },
            postCount: 2,
            hnmWindowNumber: 6);

        Assert.Equal(new[] { 1 }, tabs.Select(t => t.SequenceNumber));
        Assert.All(tabs, tab => Assert.NotNull(tab.Window));
    }

    // A post that landed beyond the counter still gets its tab: the posted set is always kept,
    // synthesis only ever ADDS to it.
    [Fact]
    public void PostsAheadOfTheCounter_AreKept()
    {
        var tabs = EventController.BuildAttendanceWindowTabs(
            new List<EventAttendanceWindowViewModel> { Posted(9) },
            postCount: 25,
            hnmWindowNumber: 2);

        Assert.Equal(new[] { 1, 2, 9 }, tabs.Select(t => t.SequenceNumber));
        Assert.NotNull(tabs.Single(t => t.SequenceNumber == 9).Window);
    }

    // Clamped to the post count, so a counter that has run past the camp's last window can't
    // produce tabs past it either.
    [Fact]
    public void CounterPastTheEnd_ClampsToThePostCount()
    {
        var tabs = EventController.BuildAttendanceWindowTabs(
            new List<EventAttendanceWindowViewModel>(),
            postCount: 4,
            hnmWindowNumber: 99);

        Assert.Equal(new[] { 1, 2, 3, 4 }, tabs.Select(t => t.SequenceNumber));
    }

    // A single-window event has nothing to strip, and must not gain a phantom "Window 1" tab
    // before anything is posted — that's what keeps the card's empty state showing.
    [Fact]
    public void SingleWindowEvent_WithNoPosts_HasNoTabs()
    {
        var tabs = EventController.BuildAttendanceWindowTabs(
            new List<EventAttendanceWindowViewModel>(),
            postCount: 1,
            hnmWindowNumber: 1);

        Assert.Empty(tabs);
    }

    // A posted window keeps its own name; only an unposted one falls back to its number.
    [Fact]
    public void PostedWindow_KeepsItsLabel()
    {
        var open = Posted(1);
        open.Label = "Open";

        var tabs = EventController.BuildAttendanceWindowTabs(
            new List<EventAttendanceWindowViewModel> { open },
            postCount: 25,
            hnmWindowNumber: 2);

        Assert.Equal("Open", tabs[0].Label);
        Assert.Equal("Window 2", tabs[1].Label);
    }
}
