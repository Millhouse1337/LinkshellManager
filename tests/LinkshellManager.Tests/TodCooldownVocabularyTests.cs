using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Utils;
using Xunit;

namespace LinkshellManager.Tests;

// Tod.Cooldown and Tod.Interval are human labels ("22 Hour", "45 Min") that RepopTime is computed
// from, so the parser that reads them back is the difference between a correct repop and a silently
// wrong one.
//
// The 84 / 71 / 2 Hour and 5 Min cases below FAILED before per-monster timings existed: the web
// form offered all six presets while TodController.ResolveCooldownHours compared against "72 Hour"
// and fell through to 22 for everything else, so five of the six stored a 22-hour repop. The same
// bug sat in SubmissionApprovalService, on the officer-approval path. Both now delegate here.
public class TodCooldownVocabularyTests
{
    [Theory]
    [InlineData("84 Hour", 84d)]
    [InlineData("72 Hour", 72d)]
    [InlineData("71 Hour", 71d)]
    [InlineData("22 Hour", 22d)]
    [InlineData("2 Hour", 2d)]
    [InlineData("5 Min", 5d / 60d)]
    public void EveryPreset_ResolvesToItsOwnLength(string label, double expectedHours) =>
        Assert.Equal(expectedHours, ActivityDataController.ResolveTodCooldownHours(label), 6);

    // The whole point of the change: a linkshell can configure an arbitrary cooldown, and the form
    // composes it as "<number> <unit>".
    [Theory]
    [InlineData("45 Min", 0.75d)]
    [InlineData("90 Min", 1.5d)]
    [InlineData("3 Hour", 3d)]
    [InlineData("1 Hour 30 Min", 1.5d)]
    public void FreeFormDurations_Resolve(string label, double expectedHours) =>
        Assert.Equal(expectedHours, ActivityDataController.ResolveTodCooldownHours(label), 6);

    // A unit-less cooldown has always meant hours — the field's own convention, and changing it
    // would silently divide every legacy "72" by 60.
    [Fact]
    public void BareNumberCooldown_MeansHours() =>
        Assert.Equal(72d, ActivityDataController.ResolveTodCooldownHours("72"), 6);

    // ...while a unit-less interval means minutes, the opposite default, for the same reason.
    [Fact]
    public void BareNumberInterval_MeansMinutes()
    {
        Assert.True(TodDurationFormat.TryParseMinutes("10", TodDurationFormat.MinutesUnit, out var minutes));
        Assert.Equal(10, minutes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("soon")]
    [InlineData("0 Hour")]
    public void UnreadableCooldown_FallsBackToTwentyTwoHours(string? label) =>
        Assert.Equal(22d, ActivityDataController.ResolveTodCooldownHours(label), 6);

    [Theory]
    [InlineData("22 Hour")]
    [InlineData("45 Min")]
    [InlineData("3.5 Hour")]
    [InlineData("72")]
    public void AcceptableCooldowns_Validate(string label) =>
        Assert.True(ActivityDataController.IsAcceptableTodCooldown(label));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("whenever")]
    [InlineData("0")]
    public void UnacceptableCooldowns_AreRejected(string? label) =>
        Assert.False(ActivityDataController.IsAcceptableTodCooldown(label));

    // An interval used to be an (hours, minutes) pair, so minutes had to be under 60. A configured
    // cadence of 90 minutes is a legitimate answer now and must not be rejected.
    [Theory]
    [InlineData("10 Min")]
    [InlineData("1 Hour")]
    [InlineData("90 Min")]
    [InlineData("2 Hour 30 Min")]
    public void AcceptableIntervals_Validate(string label) =>
        Assert.True(ActivityDataController.IsAcceptableTodInterval(label));

    // Round-tripping is what keeps the two surfaces printing the same thing for one configured
    // monster: whole hours render as hours, everything else stays in minutes, and nothing invents
    // a fractional hour.
    [Theory]
    [InlineData(1320, "22 Hour")]
    [InlineData(60, "1 Hour")]
    [InlineData(10, "10 Min")]
    [InlineData(5, "5 Min")]
    [InlineData(90, "90 Min")]
    public void Format_RoundTripsThroughTheParser(int minutes, string expectedLabel)
    {
        var label = TodDurationFormat.Format(minutes);
        Assert.Equal(expectedLabel, label);
        Assert.True(TodDurationFormat.TryParseMinutes(label, TodDurationFormat.MinutesUnit, out var parsed));
        Assert.Equal(minutes, parsed);
    }

    [Theory]
    [InlineData(1320, 22, TodDurationFormat.HoursUnit)]
    [InlineData(90, 90, TodDurationFormat.MinutesUnit)]
    [InlineData(10, 10, TodDurationFormat.MinutesUnit)]
    public void Split_MatchesFormat(int minutes, int expectedValue, string expectedUnit)
    {
        var (value, unit) = TodDurationFormat.Split(minutes);
        Assert.Equal(expectedValue, value);
        Assert.Equal(expectedUnit, unit);
        Assert.Equal(minutes, TodDurationFormat.FromValueAndUnit(value, unit));
    }

    // A missing or misspelled unit must never be read as hours, or a 22 becomes 22 hours where the
    // caller meant 22 minutes.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("furlongs")]
    public void UnknownUnit_IsTreatedAsMinutes(string? unit) =>
        Assert.Equal(22, TodDurationFormat.FromValueAndUnit(22, unit));
}
