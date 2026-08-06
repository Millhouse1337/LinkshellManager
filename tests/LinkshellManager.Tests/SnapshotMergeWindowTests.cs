using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// How close two attendance posts have to be to count as one capture of the same roster. Replaces
// the old duplicate DETECTION, which flagged the later post "PossibleDuplicate" and thereby dropped
// its members from the combined roster entirely.
public class SnapshotMergeWindowTests
{
    // The hour-long wyrm windows get the wider merge: officers trickle in over a few minutes at the
    // top of a window, and 5 minutes is still a twelfth of the window, so it cannot reach the next.
    [Theory]
    [InlineData("Tiamat")]
    [InlineData("Jormungand")]
    [InlineData("Vrtra")]
    public void TwentyFiveWindowWyrms_MergeWithinFiveMinutes(string monster) =>
        Assert.Equal(TimeSpan.FromMinutes(5), HnmConfig.SnapshotMergeWindow(monster));

    // The kings/dragons step every 10 minutes, so the merge has to stay well inside that or two
    // adjacent windows would collapse into one roster.
    [Theory]
    [InlineData("Fafnir")]
    [InlineData("Nidhogg")]
    [InlineData("Fafnir/Nidhogg")]
    [InlineData("Behemoth")]
    [InlineData("King Behemoth")]
    [InlineData("Adamantoise")]
    [InlineData("Aspidochelone")]
    public void SevenWindowKingsAndDragons_MergeWithinThreeMinutes(string monster) =>
        Assert.Equal(TimeSpan.FromMinutes(3), HnmConfig.SnapshotMergeWindow(monster));

    // No cadence to reason about (Sky gods, farm NMs, ad-hoc `/lsm now` posts) → the tighter bound.
    [Theory]
    [InlineData("Kirin")]
    [InlineData("Byakko")]
    [InlineData("Despot")]
    [InlineData("Some Random Camp")]
    [InlineData(null)]
    [InlineData("")]
    public void EverythingElse_TakesTheTighterThreeMinutes(string? monster) =>
        Assert.Equal(TimeSpan.FromMinutes(3), HnmConfig.SnapshotMergeWindow(monster));

    // The merge must never be able to span a window boundary — that's the invariant the two values
    // exist to satisfy, stated against the cadence table so adding a monster can't quietly break it.
    [Fact]
    public void MergeWindow_IsAlwaysShorterThanTheMonstersOwnWindow()
    {
        foreach (var setup in HnmConfig.WindowedHnmSetups())
        {
            var merge = HnmConfig.SnapshotMergeWindow(setup.Monster);
            Assert.True(
                merge < TimeSpan.FromMinutes(setup.Minutes),
                $"{setup.Monster}: merge window {merge} must be shorter than its {setup.Minutes}-minute spawn window.");
        }
    }
}
