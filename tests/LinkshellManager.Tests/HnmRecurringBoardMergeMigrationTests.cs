using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// One standing re-post board per SPAWN, under the combined "Base/Stronger" name. A migration merged
// the old per-half rows (MergeHnmRecurringBoardsPerSpawn, before the squash); End Camp must not
// start a new per-half row.
public class HnmRecurringBoardMergeMigrationTests
{
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
