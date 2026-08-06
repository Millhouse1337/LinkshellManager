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

    // Recurring-board recreation matches a ToD against a board by monster name. A ToD logged
    // from an HNM board copies the board's AssignedMonsterName verbatim — the COMBINED label
    // on day 4+ — so segments-only matching found nothing and the board stopped re-posting.
    // Matching must be symmetric across every spelling of the spawn.
    [Theory]
    [InlineData("Fafnir/Nidhogg")]
    [InlineData("Fafnir")]
    [InlineData("Nidhogg")]
    public void MonsterMatchNames_CoversBothHalvesAndCombined(string stored)
    {
        var matches = HnmConfig.MonsterMatchNames(stored);
        Assert.Contains("Fafnir", matches);
        Assert.Contains("Nidhogg", matches);
        Assert.Contains("Fafnir/Nidhogg", matches);
    }

    [Fact]
    public void MonsterMatchNames_SingleMonster_IsJustItself()
    {
        Assert.Equal(new[] { "Tiamat" }, HnmConfig.MonsterMatchNames("Tiamat"));
        Assert.Empty(HnmConfig.MonsterMatchNames("  "));
        Assert.Empty(HnmConfig.MonsterMatchNames(null));
    }

    [Fact]
    public void MonsterMatchNames_DoesNotBleedAcrossPairs()
    {
        var matches = HnmConfig.MonsterMatchNames("Fafnir/Nidhogg");
        Assert.DoesNotContain("Behemoth", matches);
        Assert.DoesNotContain("Adamantoise", matches);
    }

    [Fact]
    public void MonsterMatchNamesLower_IsLowerCasedForDbComparison() =>
        Assert.Contains("fafnir/nidhogg", HnmConfig.MonsterMatchNamesLower("Fafnir"));

    // The re-posted sign-up board is for the NEXT pop, so it advances the day cycle.
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(5, 6)]   // uncapped — the counter climbs until the HQ actually pops
    public void NextDayNumber_NqAdvancesByOne(int current, int expected) =>
        Assert.Equal(expected, HnmConfig.NextDayNumber(current, wasHq: false));

    // An HQ kill spends the cycle: back to day 1, which is below CombinedFromDay and so
    // also puts the board back on the NQ name.
    [Theory]
    [InlineData(5)]
    [InlineData(4)]
    [InlineData(null)]
    public void NextDayNumber_HqResetsToOne(int? current)
    {
        Assert.Equal(1, HnmConfig.NextDayNumber(current, wasHq: true));
        Assert.Equal("Adamantoise", HnmConfig.DisplayMonsterName("Adamantoise/Aspidochelone", 1));
    }

    // After an HQ kill the next spawn is the NQ half, so a board sitting on the bare stronger
    // name has to swap back. Combined labels stay combined — DisplayMonsterName already shows
    // the base half on day 1, and keeping the label lets later days reach HQ again.
    [Theory]
    [InlineData("Nidhogg", "Fafnir")]
    [InlineData("King Behemoth", "Behemoth")]
    [InlineData("Aspidochelone", "Adamantoise")]
    [InlineData("Fafnir", "Fafnir")]                                    // already NQ
    [InlineData("Fafnir/Nidhogg", "Fafnir/Nidhogg")]                    // combined: unchanged
    [InlineData("Tiamat", "Tiamat")]                                    // no NQ/HQ split
    public void BaseMonsterName_MapsStrongerHalfToItsBase(string input, string expected) =>
        Assert.Equal(expected, HnmConfig.BaseMonsterName(input));

    // Monsters with no day cycle must not sprout a "Day 1" tile on the re-post.
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-3)]
    public void NextDayNumber_NoDayStaysNull(int? current) =>
        Assert.Null(HnmConfig.NextDayNumber(current, wasHq: false));

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
        Assert.Equal(2, HnmConfig.GetWindowCount(monster));  // ShortWindow (Open / Close)
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

    // The create-event form's HNM / NM buttons cut the dropdown in two. HNM is the three
    // long-window wyrms plus the three NQ/HQ families -- six entries, exactly what the addon's
    // preset panel calls "HNMS (6)"; NM is everything else.
    [Theory]
    [InlineData("Tiamat", true)]
    [InlineData("Jormungand", true)]
    [InlineData("Vrtra", true)]
    [InlineData("Adamantoise/Aspidochelone", true)]
    [InlineData("Behemoth/King Behemoth", true)]
    [InlineData("Fafnir/Nidhogg", true)]
    [InlineData("Adamantoise", true)]   // either half alone resolves the same way
    [InlineData("Nidhogg", true)]
    [InlineData("King Arthro", false)]
    [InlineData("Bloodsucker", false)]
    [InlineData("Xolotl", false)]
    // The timed NMs are in ShortWindowHnms for their 7 x 10-min spawn band and are STILL NMs.
    // Tier and cadence are different questions; reading membership of that set as the tier
    // would file all four under HNM.
    [InlineData("Capricious Cassie", false)]
    [InlineData("Bune", false)]
    [InlineData("Boroka", false)]
    [InlineData("Roc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHnmTierMonster_SplitsTheDropdown(string? monster, bool isHnmTier) =>
        Assert.Equal(isHnmTier, HnmConfig.IsHnmTierMonster(monster));

    // Every option the form offers lands on exactly one side of the split, and the HNM side is
    // the six the addon lists. A monster added to SupportedMonsters without a thought about
    // tier shows up here as a change in one of these two counts.
    [Fact]
    public void TheTwoTiers_PartitionTheWholeDropdown()
    {
        var options = HnmConfig.CombinedMonsterOptions(TodManagerViewModel.SupportedMonsters);
        var hnmTier = options.Where(HnmConfig.IsHnmTierMonster).ToArray();
        var nmTier = options.Where(m => !HnmConfig.IsHnmTierMonster(m)).ToArray();

        Assert.Equal(options.Count, hnmTier.Length + nmTier.Length);
        Assert.Equal(
            new[]
            {
                "Adamantoise/Aspidochelone", "Behemoth/King Behemoth", "Fafnir/Nidhogg",
                "Jormungand", "Tiamat", "Vrtra",
            },
            hnmTier.OrderBy(m => m, StringComparer.Ordinal).ToArray());
        Assert.Equal(11, nmTier.Length);
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
