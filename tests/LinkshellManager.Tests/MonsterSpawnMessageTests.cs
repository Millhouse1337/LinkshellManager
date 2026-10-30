using System.Linq;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The spawn-message hover text on the Monster Setups names: the chat lines a monster's pop prints,
// which the addon's Claim Shield watches for.
public class MonsterSpawnMessageTests
{
    // A merged row shows BOTH halves' lines: either half can be the one that pops.
    [Fact]
    public void SpawnMessages_MergedRowGetsBothHalves()
    {
        var messages = HnmConfig.ClaimShieldSpawnMessagesFor("Fafnir/Nidhogg");

        Assert.Equal(new[] { "Fafnir", "Nidhogg" }, messages.Select(m => m.Monster));
        Assert.Contains(messages, m => m.Text == "You hear an ominous roar off in the distance");
    }

    // A monster with no known spawn line gets no hover at all, rather than an empty one.
    [Theory]
    [InlineData("Tiamat")]
    [InlineData("Ouryu")]
    [InlineData(null)]
    public void SpawnMessages_NoneForAMonsterWithoutOne(string? monsterName)
    {
        Assert.Empty(HnmConfig.ClaimShieldSpawnMessagesFor(monsterName));
    }
}
