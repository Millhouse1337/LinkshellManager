using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the per-linkshell DKP rounding grid that loot spends/refunds and the
// per-hour event award all snap to.
public class DkpRoundingTests
{
    [Theory]
    [InlineData("Half", 0.5)]
    [InlineData("half", 0.5)]          // case-insensitive
    [InlineData("Quarter", 0.25)]
    [InlineData("", 0.25)]             // unset -> default Quarter
    [InlineData(null, 0.25)]
    [InlineData("something-else", 0.25)]
    public void StepFor_MapsTheIncrementSetting(string? increment, double expected)
    {
        Assert.Equal(expected, DkpRounding.StepFor(increment));
    }

    [Theory]
    [InlineData(16.3, 0.25, 16.25)]
    [InlineData(16.4, 0.25, 16.5)]
    [InlineData(16.12, 0.25, 16.0)]
    [InlineData(16.8, 0.5, 17.0)]
    [InlineData(16.7, 0.5, 16.5)]
    [InlineData(42, 0.25, 42)]
    public void Round_SnapsToNearestStep(double value, double step, double expected)
    {
        Assert.Equal(expected, DkpRounding.Round(value, step), precision: 5);
    }

    [Fact]
    public void Round_NonPositiveStep_ReturnsValueUnchanged()
    {
        Assert.Equal(17.37, DkpRounding.Round(17.37, 0));
    }
}
