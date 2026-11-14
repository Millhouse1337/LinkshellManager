using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Loot = LinkshellManagerDiscordApp.Controllers.ActivityMappers;
using Tods = LinkshellManagerDiscordApp.Controllers.ActivityWindowEventsController;

namespace LinkshellManager.Tests;

// Pins two small rules that services borrow from controllers: how a linkshell's loot structure
// text is cleaned up (which decides whether loot costs DKP), and the default ToD cooldown /
// interval labels for a monster (which decide when a repop is due).
//
// Why: both are being moved out of the controllers into plain classes in Services, and the move
// must not change any answer. The two `using ... =` aliases at the top are the only lines that
// should change when they move. (ResolveTodCooldownHours and the IsAcceptable checks are already
// pinned by TodCooldownVocabularyTests.)
public class LootStructureAndTodDefaultsPinTests
{
    // Exact, case-insensitive matches only; anything else -- spaces included -- is plain DKP.
    [Theory]
    [InlineData("LootCouncil", "LootCouncil")]
    [InlineData("lootcouncil", "LootCouncil")]
    [InlineData("LOOTCOUNCIL", "LootCouncil")]
    [InlineData("Hybrid", "Hybrid")]
    [InlineData("hybrid", "Hybrid")]
    [InlineData("Dkp", "Dkp")]
    [InlineData("DKP", "Dkp")]
    [InlineData("", "Dkp")]
    [InlineData(" LootCouncil ", "Dkp")]
    [InlineData("Loot Council", "Dkp")]
    [InlineData("anything", "Dkp")]
    public void LootStructure_Normalizes(string stored, string expected) =>
        Assert.Equal(expected, Loot.NormalizeLootStructure(stored));

    // The built-in labels for a linkshell that never configured the monster.
    [Theory]
    [InlineData("Tiamat", "84 Hour", "1 Hour")]
    [InlineData("Cerberus", "48 Hour", "6 Hour")]
    [InlineData("Fafnir/Nidhogg", "22 Hour", "10 Min")]
    [InlineData("King Behemoth", "22 Hour", "10 Min")]
    [InlineData("Kirin", "5 Min", "10 Min")]
    [InlineData("Bloodsucker", "71 Hour", "10 Min")]
    [InlineData("Some Custom NM", "22 Hour", "10 Min")]
    [InlineData(null, "22 Hour", "10 Min")]
    public void BuiltInDefaults(string? monster, string cooldown, string interval)
    {
        Assert.Equal(cooldown, Tods.GetDefaultTodCooldown(monster));
        Assert.Equal(interval, Tods.GetDefaultTodInterval(monster));
    }

    // With no setup rows the per-linkshell answer is the built-in one; with a row it is the row's.
    [Fact]
    public async Task PerLinkshellDefaults_ReadTheMonsterSetup()
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.LinkshellMonsterTimings.Add(new LinkshellMonsterTiming
        {
            LinkshellId = 7,
            MonsterName = "Serket",
            NormalizedMonsterName = MonsterTimingDefaults.Normalize("Serket"),
            WindowCount = 4,
            WindowCadenceMinutes = 30,
            CooldownMinutes = 21 * 60 + 15,
            IsCustom = true,
        });
        await db.SaveChangesAsync();
        var resolver = new MonsterTimingResolver(db);

        Assert.Equal("84 Hour", await Tods.GetDefaultTodCooldownAsync(resolver, 99, "Tiamat", CancellationToken.None));
        Assert.Equal("1 Hour", await Tods.GetDefaultTodIntervalAsync(resolver, 99, "Tiamat", CancellationToken.None));
        Assert.Equal("1275 Min", await Tods.GetDefaultTodCooldownAsync(resolver, 7, "Serket", CancellationToken.None));
        Assert.Equal("30 Min", await Tods.GetDefaultTodIntervalAsync(resolver, 7, "Serket", CancellationToken.None));
    }
}
