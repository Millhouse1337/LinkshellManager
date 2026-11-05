using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// One standing re-post board per SPAWN, under the combined "Base/Stronger" name. The migration
// merges the old per-half rows; End Camp must not start a new per-half row afterwards.
public class HnmRecurringBoardMergeMigrationTests
{
    [Fact]
    public void Migration_CoversExactlyTheMergePairs()
    {
        Assert.Equal(HnmConfig.MonsterMergePairs.ToArray(), MergeHnmRecurringBoardsPerSpawn.Pairs);
    }

    [Fact]
    public void MergeSql_KeepsTheEnabledThenNewestRow_AndRenamesItToTheCombinedName()
    {
        var sql = MergeHnmRecurringBoardsPerSpawn.MergeSql("Adamantoise", "Aspidochelone");

        Assert.Contains("'adamantoise', 'aspidochelone', 'adamantoise/aspidochelone'", sql);
        Assert.Contains(@"ORDER BY ""Enabled"" DESC, ""UpdatedAt"" DESC, ""Id"" DESC", sql);
        Assert.Contains(@"SET ""MonsterName"" = 'Adamantoise/Aspidochelone'", sql);
        // Delete before rename: (LinkshellId, MonsterName) is unique.
        Assert.True(sql.IndexOf("DELETE", StringComparison.Ordinal) < sql.IndexOf("UPDATE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Adamantoise", "Adamantoise/Aspidochelone")]
    [InlineData("Nidhogg", "Fafnir/Nidhogg")]
    [InlineData("Tiamat", "Tiamat")]
    public async Task EndCamp_CreatesTheBoardUnderTheSpawnsName(string campMonster, string expected)
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var ev = new Event
        {
            Id = 1, LinkshellId = 4, EventName = "Camp", EventType = "HNM", AssignedMonsterName = campMonster,
        };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        await HnmRecurringBoardService.ApplyEndCampChoiceAsync(db, ev, campMonster, enabled: true, leadHours: 3, CancellationToken.None);

        var board = await db.HnmRecurringBoards.SingleAsync();
        Assert.Equal(expected, board.MonsterName);
        Assert.Equal(3, board.LeadHours);
    }
}
