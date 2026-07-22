using System;
using System.Linq;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the HNM monster merge model: the create-event dropdown folds each "stronger" half
// (Aspidochelone/King Behemoth/Nidhogg) into its base entry, and from CombinedFromDay on it
// stores a combined "Base/Stronger" name. Every name lookup must stay tolerant of that
// combined form so zone/window/routing/recurrence don't silently break.
public class HnmMonsterMergeTests
{
    [Fact]
    public void MonsterSegments_SplitsCombined_AndSingles()
    {
        Assert.Equal(new[] { "Adamantoise", "Aspidochelone" }, HnmConfig.MonsterSegments("Adamantoise/Aspidochelone"));
        Assert.Equal(new[] { "Adamantoise" }, HnmConfig.MonsterSegments("Adamantoise"));
        Assert.Empty(HnmConfig.MonsterSegments("  "));
        Assert.Empty(HnmConfig.MonsterSegments(null));
    }

    [Theory]
    [InlineData("Adamantoise/Aspidochelone", "Qufim Island")]
    [InlineData("Behemoth/King Behemoth", "Behemoth's Dominion")]
    [InlineData("Fafnir/Nidhogg", "Dragon's Aery")]
    [InlineData("Adamantoise", "Qufim Island")]      // single still resolves
    [InlineData("Aspidochelone", "Qufim Island")]    // stronger half still resolves
    public void ZoneFor_ResolvesCombinedAndSingle(string monster, string expectedZone)
    {
        Assert.Equal(expectedZone, HnmConfig.ZoneFor(monster));
    }

    [Fact]
    public void ZoneFor_UnknownMonster_IsNull() =>
        Assert.Null(HnmConfig.ZoneFor("Some Random NM"));

    [Theory]
    [InlineData("Adamantoise/Aspidochelone")]
    [InlineData("Behemoth/King Behemoth")]
    [InlineData("Fafnir/Nidhogg")]
    public void CombinedNames_AreRecognized(string monster)
    {
        Assert.True(HnmConfig.IsTrueHnm(monster));           // still a curated HNM
        Assert.Equal(2, HnmConfig.GetWindowCount(monster));  // ShortWindow (On Time / Claim/Kill)
        Assert.False(HnmConfig.SupportsWindowAdvance(monster)); // none of the pairs are LongWindow
    }

    [Fact]
    public void SupportsWindowAdvance_StillTrueForLongWindow() =>
        Assert.True(HnmConfig.SupportsWindowAdvance("Tiamat"));

    [Fact]
    public void MergedStrongerMonsters_AreTheThreeStrongerHalves()
    {
        Assert.Contains("Aspidochelone", HnmConfig.MergedStrongerMonsters);
        Assert.Contains("King Behemoth", HnmConfig.MergedStrongerMonsters);
        Assert.Contains("Nidhogg", HnmConfig.MergedStrongerMonsters);
        Assert.Equal(3, HnmConfig.MergedStrongerMonsters.Count);
    }

    [Fact]
    public void MergePairs_MatchExpected()
    {
        Assert.Equal(3, HnmConfig.MonsterMergePairs.Count);
        Assert.Contains(("Adamantoise", "Aspidochelone"), HnmConfig.MonsterMergePairs);
        Assert.Contains(("Behemoth", "King Behemoth"), HnmConfig.MonsterMergePairs);
        Assert.Contains(("Fafnir", "Nidhogg"), HnmConfig.MonsterMergePairs);
    }

    // The create-event dropdown always shows each pair as ONE combined entry; the stronger
    // halves and the bare base names are not standalone options. (Mirrors EventController.)
    [Fact]
    public void CombinedMonsterOptions_ShowsCombinedPairs_ExcludesStrongersAndBareBases()
    {
        var options = HnmConfig.CombinedMonsterOptions(TodManagerViewModel.SupportedMonsters);

        Assert.Contains("Adamantoise/Aspidochelone", options);
        Assert.Contains("Behemoth/King Behemoth", options);
        Assert.Contains("Fafnir/Nidhogg", options);
        Assert.DoesNotContain("Adamantoise", options);   // no bare base entry
        Assert.DoesNotContain("Aspidochelone", options); // stronger folded in
        Assert.DoesNotContain("King Behemoth", options);
        Assert.DoesNotContain("Nidhogg", options);
        // Unrelated monsters are untouched and keep their order.
        Assert.Contains("Tiamat", options);
        Assert.Contains("Xolotl", options);
    }

    // The board display collapses a combined pair to its base below the day threshold, and
    // shows the full pair at/above it (and when no day is given). Stored name is unchanged.
    [Theory]
    [InlineData(1, "Adamantoise")]
    [InlineData(3, "Adamantoise")]
    [InlineData(4, "Adamantoise/Aspidochelone")]
    [InlineData(9, "Adamantoise/Aspidochelone")]
    public void DisplayMonsterName_CollapsesCombinedByDay(int day, string expected) =>
        Assert.Equal(expected, HnmConfig.DisplayMonsterName("Adamantoise/Aspidochelone", day));

    [Fact]
    public void DisplayMonsterName_NoDay_ShowsCombined() =>
        Assert.Equal("Adamantoise/Aspidochelone", HnmConfig.DisplayMonsterName("Adamantoise/Aspidochelone", null));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void DisplayMonsterName_SingleMonster_Unchanged(int day) =>
        Assert.Equal("Tiamat", HnmConfig.DisplayMonsterName("Tiamat", day));
}
