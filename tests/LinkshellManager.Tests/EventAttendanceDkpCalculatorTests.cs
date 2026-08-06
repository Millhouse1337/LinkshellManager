using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// What an event pays ONE participant at close. Two models that must never blend: timed events pay
// durationHours × rate (break-aware, because durationHours excludes paused time), windowed HNM
// camps pay windowsAttended × rate and must ignore the clock entirely.
//
// Shared by BOTH end-event paths (EventController.EndEventCoreAsync and
// ActivityDataController.EndEventAsync). The Activity path used to have no windowed branch at all,
// which made it the one place where break state still moved windowed DKP.
public class EventAttendanceDkpCalculatorTests
{
    private const double Quarter = DkpRounding.QuarterStep; // 0.25

    // ---------------------------------------------------------------- windowed ---

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(1, 1.0)]
    [InlineData(3, 3.0)]
    [InlineData(7, 7.0)]
    public void Windowed_PaysPerWindowAttended(int windows, double expected)
    {
        var dkp = EventAttendanceDkpCalculator.Compute(
            isWindowed: true, windowsAttended: windows,
            durationHours: 9.5, dkpPerHour: 1.0, roundingStep: Quarter);

        Assert.Equal(expected, dkp, precision: 3);
    }

    // THE regression this whole change exists for. Same windows attended, wildly different clock
    // time (a member who took a two-hour break vs one who never left): identical payout.
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(4.0)]
    [InlineData(72.0)]
    public void Windowed_IgnoresDurationEntirely(double durationHours)
    {
        var dkp = EventAttendanceDkpCalculator.Compute(
            isWindowed: true, windowsAttended: 3,
            durationHours: durationHours, dkpPerHour: 2.0, roundingStep: Quarter);

        Assert.Equal(6.0, dkp, precision: 3);
    }

    // A fractional per-window rate (Manual Check In linkshells run 0.25) must survive intact — snapping the
    // product to the rounding grid would re-scale their payouts.
    [Fact]
    public void Windowed_PreservesAFractionalPerWindowRate()
    {
        var dkp = EventAttendanceDkpCalculator.Compute(
            isWindowed: true, windowsAttended: 5,
            durationHours: 0, dkpPerHour: 0.25, roundingStep: DkpRounding.QuarterStep);

        Assert.Equal(1.25, dkp, precision: 3);
    }

    [Fact]
    public void Windowed_NegativeWindowCountFloorsAtZero()
    {
        var dkp = EventAttendanceDkpCalculator.Compute(
            isWindowed: true, windowsAttended: -3,
            durationHours: 5, dkpPerHour: 1.0, roundingStep: Quarter);

        Assert.Equal(0.0, dkp, precision: 3);
    }

    // ------------------------------------------------------------------- timed ---

    [Fact]
    public void Timed_PaysForTimePresent()
    {
        var dkp = EventAttendanceDkpCalculator.Compute(
            isWindowed: false, windowsAttended: 0,
            durationHours: 3.0, dkpPerHour: 2.0, roundingStep: Quarter);

        Assert.Equal(6.0, dkp, precision: 3);
    }

    // Timed events DO respond to break state, via durationHours. This is the contrast case, and
    // the reason the Break Room exists for them and only them.
    [Fact]
    public void Timed_ShorterDurationPaysLess()
    {
        var full = EventAttendanceDkpCalculator.Compute(
            isWindowed: false, windowsAttended: 0,
            durationHours: 4.0, dkpPerHour: 1.0, roundingStep: Quarter);
        var paused = EventAttendanceDkpCalculator.Compute(
            isWindowed: false, windowsAttended: 0,
            durationHours: 2.0, dkpPerHour: 1.0, roundingStep: Quarter);

        Assert.Equal(4.0, full, precision: 3);
        Assert.Equal(2.0, paused, precision: 3);
    }

    // Rounding lands on the DKP value, not the duration. Rounding the duration first floored
    // sub-quarter-hour events to 0h and paid present members nothing.
    [Fact]
    public void Timed_RoundsTheDkpValueNotTheDuration()
    {
        // 0.1h × 1 DKP/h = 0.1 → snaps up to the quarter grid rather than flooring the duration
        // to 0h and paying 0.
        var dkp = EventAttendanceDkpCalculator.Compute(
            isWindowed: false, windowsAttended: 0,
            durationHours: 0.1, dkpPerHour: 1.0, roundingStep: Quarter);

        Assert.Equal(DkpRounding.Round(0.1, Quarter), dkp, precision: 3);
    }

    // A windowed event's windowsAttended must not leak into the timed branch.
    [Fact]
    public void Timed_IgnoresWindowsAttended()
    {
        var dkp = EventAttendanceDkpCalculator.Compute(
            isWindowed: false, windowsAttended: 99,
            durationHours: 2.0, dkpPerHour: 1.0, roundingStep: Quarter);

        Assert.Equal(2.0, dkp, precision: 3);
    }

    [Fact]
    public void ZeroRate_PaysNothingOnEitherModel()
    {
        Assert.Equal(0.0, EventAttendanceDkpCalculator.Compute(true, 5, 5, 0, Quarter), precision: 3);
        Assert.Equal(0.0, EventAttendanceDkpCalculator.Compute(false, 5, 5, 0, Quarter), precision: 3);
    }
}
