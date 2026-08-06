using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The Manual Check In earn formula (the pure core of WdCampFinalizer): a member is credited every window
// from the one they first x'd in through the last window the camp ran, at WdDkpPerWindow each, plus
// claim/kill bonuses, snapped to the linkshell's rounding grid. The canonical example comes straight
// from the WhereDragon explanation: "a full 7 window fafnir with no claim would be 0.25 per window
// for a total of 1.75 dkp".
public class WdCampFinalizerTests
{
    private const double Quarter = DkpRounding.QuarterStep; // 0.25

    [Theory]
    [InlineData(1, 7, 7)]   // present from the start of a 7-window camp → all 7
    [InlineData(3, 7, 5)]   // arrived at window 3 → windows 3..7 = 5
    [InlineData(7, 7, 1)]   // arrived on the final window → just 1
    [InlineData(1, 1, 1)]   // single-window camp
    [InlineData(2, 5, 4)]   // checked out: arrival 2, last credited 5 → W2,3,4,5 = 4 (the user's case)
    [InlineData(1, 4, 4)]   // arrival 1, checked out end of window 4 → W1,2,3,4 = 4
    public void WindowsCredited_CountsArrivalThroughLast(int arrival, int last, int expected)
    {
        Assert.Equal(expected, WdCampFinalizer.WindowsCredited(arrival, last));
    }

    [Theory]
    [InlineData(0, 7, 7)]    // arrival below 1 clamps up to window 1 → all 7
    [InlineData(9, 7, 0)]    // arrived AFTER the last credited window (e.g. after the pop) → 0
    [InlineData(6, 3, 0)]    // checked in window 6 but it popped on window 3 → 0
    [InlineData(3, 0, 0)]    // a zero/absent last window collapses to 1; arrival 3 is past it → 0
    public void WindowsCredited_ArrivalPastLast_IsZero(int arrival, int last, int expected)
    {
        Assert.Equal(expected, WdCampFinalizer.WindowsCredited(arrival, last));
    }

    [Fact]
    public void ComputeDkp_Full7WindowFafnir_NoClaimNoKill_Is1point75()
    {
        // The WhereDragon acceptance case.
        var dkp = WdCampFinalizer.ComputeDkp(
            rate: 0.25, arrivalWindow: 1, lastWindow: 7,
            campLastWindow: 7, openBonus: 0, closeBonus: 0,
            claimBonus: 0, killBonus: 0, step: Quarter);

        Assert.Equal(1.75, dkp, precision: 3);
    }

    [Fact]
    public void ComputeDkp_LateArrival_CreditsOnlyWindowsPresent()
    {
        // Arrived at window 3 of a 7-window camp → 5 windows × 0.25 = 1.25.
        var dkp = WdCampFinalizer.ComputeDkp(
            rate: 0.25, arrivalWindow: 3, lastWindow: 7,
            campLastWindow: 7, openBonus: 0, closeBonus: 0,
            claimBonus: 0, killBonus: 0, step: Quarter);

        Assert.Equal(1.25, dkp, precision: 3);
    }

    [Fact]
    public void ComputeDkp_AddsClaimAndKillBonuses()
    {
        // 7 windows × 0.25 = 1.75, + claim 0.25 + kill 0.5 = 2.5.
        var dkp = WdCampFinalizer.ComputeDkp(
            rate: 0.25, arrivalWindow: 1, lastWindow: 7,
            campLastWindow: 7, openBonus: 0, closeBonus: 0,
            claimBonus: 0.25, killBonus: 0.5, step: Quarter);

        Assert.Equal(2.5, dkp, precision: 3);
    }

    [Fact]
    public void ComputeDkp_SnapsToTheRoundingGrid()
    {
        // 3 windows × 0.1 = 0.30 → snapped to the nearest 0.25 = 0.25.
        var dkp = WdCampFinalizer.ComputeDkp(
            rate: 0.1, arrivalWindow: 5, lastWindow: 7,
            campLastWindow: 7, openBonus: 0, closeBonus: 0,
            claimBonus: 0, killBonus: 0, step: Quarter);

        Assert.Equal(0.25, dkp, precision: 3);
    }

    [Fact]
    public void ComputeDkp_HalfStepLinkshell_SnapsToHalves()
    {
        // 7 windows × 0.2 = 1.4 → snapped to the nearest 0.5 = 1.5.
        var dkp = WdCampFinalizer.ComputeDkp(
            rate: 0.2, arrivalWindow: 1, lastWindow: 7,
            campLastWindow: 7, openBonus: 0, closeBonus: 0,
            claimBonus: 0, killBonus: 0, step: DkpRounding.HalfStep);

        Assert.Equal(1.5, dkp, precision: 3);
    }

    // The Manual Check In counterparts of the Standard open / close bonuses. They gate on the
    // member's own check-in RANGE, which is the only presence record this mode has: open means
    // they were checked in from window 1, close means they were still checked in at the camp's
    // last credited window. Both default to 0, so every case above is unaffected.
    [Theory]
    // arrival, lastCredited, campLast, expected
    [InlineData(1, 7, 7, 1.75 + 1.0 + 0.5)]  // whole camp → both bonuses
    [InlineData(3, 7, 7, 1.25 + 0.5)]        // joined late, stayed → close only
    [InlineData(1, 4, 7, 1.00 + 1.0)]        // there at the open, checked out early → open only
    [InlineData(3, 5, 7, 0.75)]              // neither end → the per-window rate alone
    public void ComputeDkp_OpenAndCloseBonuses_GateOnTheCheckInRange(
        int arrival, int lastCredited, int campLast, double expected)
    {
        var dkp = WdCampFinalizer.ComputeDkp(
            rate: 0.25, arrivalWindow: arrival, lastWindow: lastCredited,
            campLastWindow: campLast, openBonus: 1.0, closeBonus: 0.5,
            claimBonus: 0, killBonus: 0, step: Quarter);

        Assert.Equal(expected, dkp, precision: 3);
    }

    // Someone who checked in after the camp's last credited window earns NOTHING — not even the
    // open bonus their clamped arrival would otherwise qualify for. They were never present for a
    // window the payout covers, so paying them for the open would credit an empty range.
    [Fact]
    public void ComputeDkp_ArrivedAfterTheCampEnded_EarnsNoBonuses()
    {
        var dkp = WdCampFinalizer.ComputeDkp(
            rate: 0.25, arrivalWindow: 6, lastWindow: 3,
            campLastWindow: 3, openBonus: 1.0, closeBonus: 0.5,
            claimBonus: 0.25, killBonus: 0.25, step: Quarter);

        Assert.Equal(0d, dkp, precision: 3);
    }
}

// Timed window auto-advance schedule: window N opens at anchor + (N-1)*minutes, clamped to the
// camp's window count. Drives HnmWindowAdvanceBackgroundService's live-camp advance.
public class WdWindowScheduleTests
{
    private static readonly DateTime Start = new(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 1)]     // at the pop → window 1
    [InlineData(14, 1)]    // 14 min in, 15-min windows → still window 1
    [InlineData(15, 2)]    // exactly one window elapsed → window 2
    [InlineData(31, 3)]    // 31 min → window 3
    [InlineData(90, 7)]    // 90 min → window 7 (of 7)
    [InlineData(300, 7)]   // way past the end → clamped to 7
    public void ScheduledWindow_AdvancesOnCadence_ClampedToCount(int minutesElapsed, int expected)
    {
        var now = Start.AddMinutes(minutesElapsed);
        Assert.Equal(expected, HnmWindowAdvanceBackgroundService.ScheduledWindow(Start, now, minutes: 15, windowCount: 7));
    }

    [Fact]
    public void ScheduledWindow_BeforeStart_IsWindowOne()
    {
        Assert.Equal(1, HnmWindowAdvanceBackgroundService.ScheduledWindow(Start, Start.AddMinutes(-5), minutes: 15, windowCount: 7));
    }

    [Fact]
    public void ScheduledWindow_ZeroMinutes_StaysWindowOne()
    {
        // minutes = 0 means manual advance — the schedule never moves it.
        Assert.Equal(1, HnmWindowAdvanceBackgroundService.ScheduledWindow(Start, Start.AddHours(3), minutes: 0, windowCount: 7));
    }

    // Short-window kings/dragons: 10-min cadence × 7 windows = window 1 at the pop through window 7
    // a full hour later.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(60, 7)]    // 60 min → window 7 (of 7)
    [InlineData(120, 7)]   // way past → clamped to 7
    public void ScheduledWindow_ShortWindowCadence(int minutesElapsed, int expected)
    {
        var now = Start.AddMinutes(minutesElapsed);
        Assert.Equal(expected, HnmWindowAdvanceBackgroundService.ScheduledWindow(Start, now, minutes: 10, windowCount: 7));
    }
}
