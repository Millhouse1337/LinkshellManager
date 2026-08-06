using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Placing a moment on a camp's window grid. Window N runs [anchor + (N-1)×cadence, anchor + N×
// cadence), and this one mapping is shared by the live board's auto-advance and by the window label
// stamped on every attendance snapshot — so a snapshot can never disagree with the board it came from.
public class SnapshotWindowNumberTests
{
    private static readonly DateTime Anchor = new(2026, 8, 3, 9, 0, 0, DateTimeKind.Utc);

    // A 10-minute grid: window 1 covers 9:00-9:10, window 2 covers 9:10-9:20, and so on. The
    // boundary belongs to the window it OPENS, not the one it closes.
    [Theory]
    [InlineData(0, 1)]      // exactly at the anchor
    [InlineData(1, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 2)]     // boundary opens window 2
    [InlineData(19, 2)]
    [InlineData(20, 3)]
    [InlineData(55, 6)]
    [InlineData(60, 7)]     // final window of a 7-window camp
    public void TenMinuteGrid_BucketsByInterval(int minutesAfterAnchor, int expected) =>
        Assert.Equal(expected, HnmConfig.WindowNumberAt(
            Anchor, Anchor.AddMinutes(minutesAfterAnchor), minutes: 10, windowCount: 7));

    // The bucket a scan LANDS in, not the boundary nearest to it. 9:58 is two minutes from window
    // 7's start but window 6 is the one that was actually open, and that's what was scanned.
    [Fact]
    public void NearABoundary_TakesTheWindowThatWasOpen_NotTheNearestOne() =>
        Assert.Equal(6, HnmConfig.WindowNumberAt(
            Anchor, Anchor.AddMinutes(58), minutes: 10, windowCount: 7));

    // Hourly grid for the wyrms: a capture 3h20m into the camp is window 4.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(59, 1)]
    [InlineData(60, 2)]
    [InlineData(200, 4)]    // 3h20m
    [InlineData(1440, 25)]  // 24h -> the final window
    public void HourlyGrid_BucketsByInterval(int minutesAfterAnchor, int expected) =>
        Assert.Equal(expected, HnmConfig.WindowNumberAt(
            Anchor, Anchor.AddMinutes(minutesAfterAnchor), minutes: 60, windowCount: 25));

    // A camp that runs long can't produce a window past its own count, and a capture timestamped
    // before the anchor (clock skew, a manually back-dated post) lands on window 1 rather than zero.
    [Theory]
    [InlineData(10_000, 7)]
    [InlineData(-30, 1)]
    public void OutOfRangeMoments_AreClamped(int minutesAfterAnchor, int expected) =>
        Assert.Equal(expected, HnmConfig.WindowNumberAt(
            Anchor, Anchor.AddMinutes(minutesAfterAnchor), minutes: 10, windowCount: 7));

    [Fact]
    public void NoCadence_IsAlwaysWindowOne() =>
        Assert.Equal(1, HnmConfig.WindowNumberAt(
            Anchor, Anchor.AddHours(9), minutes: 0, windowCount: 7));

    // The per-monster wrapper: cadence comes from the camp's name, including a combined
    // "Base/Stronger" label.
    [Theory]
    [InlineData("Tiamat", 200, 4)]                    // 60-min windows
    [InlineData("Jormungand", 59, 1)]
    [InlineData("Vrtra", 60, 2)]
    [InlineData("Fafnir", 25, 3)]                     // 10-min windows
    [InlineData("Fafnir/Nidhogg", 25, 3)]
    [InlineData("Behemoth/King Behemoth", 45, 5)]
    [InlineData("Adamantoise", 9, 1)]
    public void SnapshotWindowNumber_UsesTheMonstersOwnCadence(string monster, int minutesAfter, int expected) =>
        Assert.Equal(expected, HnmConfig.SnapshotWindowNumber(
            monster, Anchor, Anchor.AddMinutes(minutesAfter)));

    // NULL, not 1. A camp with no cadence has no window grid at all, and labelling every Kirin
    // snapshot "Window 1" would invent structure that doesn't exist.
    [Theory]
    [InlineData("Kirin")]
    [InlineData("Byakko")]
    [InlineData("Despot")]
    [InlineData("Some Random Camp")]
    [InlineData(null)]
    public void SnapshotWindowNumber_IsNullWithoutACadence(string? monster) =>
        Assert.Null(HnmConfig.SnapshotWindowNumber(monster, Anchor, Anchor.AddMinutes(45)));

    // The board's advancer and the snapshot labeller must be the same function, or a snapshot could
    // claim a different window than the board was showing when it was taken.
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(61)]
    [InlineData(600)]
    public void BoardAdvanceAndSnapshotLabelling_AgreeExactly(int minutesAfter)
    {
        var at = Anchor.AddMinutes(minutesAfter);
        Assert.Equal(
            HnmWindowAdvanceBackgroundService.ScheduledWindow(Anchor, at, minutes: 60, windowCount: 25),
            HnmConfig.SnapshotWindowNumber("Tiamat", Anchor, at));
    }
}
