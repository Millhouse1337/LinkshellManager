using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// A camp "created itself": an officer deleted a Fafnir board that had Repeat-on-ToD on, then logged
// a ToD -- and the poller posted a brand-new board from the template the deleted event had left
// armed, under that event's name, party setup and location. Removing a board has to stop it
// repeating.
public class HnmRemovedBoardStopsRepeatingTests
{
    private const int LinkshellId = 1;
    private static readonly DateTime Repop = new(2026, 9, 19, 9, 43, 57, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private static Event Board(int id, string monster = "Fafnir") => new()
    {
        Id = id, LinkshellId = LinkshellId, EventName = "faf", EventType = "HNM",
        EventLocation = "Dragon's Aery", AssignedMonsterName = monster, StartTime = Repop.AddHours(-22),
    };

    private static async Task<ApplicationDbContext> SeededAsync(string name, params Event[] boards)
    {
        var db = NewInMemoryContext(name);
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.Events.AddRange(boards);
        db.HnmRecurringBoards.Add(new HnmRecurringBoard
        {
            Id = 7, LinkshellId = LinkshellId, MonsterName = "Fafnir", Enabled = true,
            LeadHours = 1, EventNameTemplate = "faf", EventLocation = "Dragon's Aery",
        });
        await db.SaveChangesAsync();
        return db;
    }

    // What the four removal endpoints do: stop the repeat, remove the row, save once.
    private static async Task RemoveAsync(ApplicationDbContext db, int eventId)
    {
        var ev = await db.Events.SingleAsync(e => e.Id == eventId);
        await HnmRecurringBoardService.StopRepeatingForRemovedBoardAsync(db, ev, CancellationToken.None);
        db.Events.Remove(ev);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task RemovingTheOnlyBoard_TurnsItsRepeatOff()
    {
        using var db = await SeededAsync(Guid.NewGuid().ToString(), Board(100));

        await RemoveAsync(db, 100);

        Assert.False((await db.HnmRecurringBoards.SingleAsync()).Enabled);
    }

    // The reported sequence end to end: once the board is removed, a fresh ToD inside the lead
    // window posts nothing.
    [Fact]
    public async Task AfterTheBoardIsRemoved_ANewTodPostsNothing()
    {
        using var db = await SeededAsync(Guid.NewGuid().ToString(), Board(100));
        await RemoveAsync(db, 100);
        db.Tods.Add(new Tod
        {
            Id = 60, LinkshellId = LinkshellId, MonsterName = "Fafnir",
            Time = Repop.AddHours(-22), RepopTime = Repop,
        });
        await db.SaveChangesAsync();

        // The poller only walks ENABLED templates; this is the one it would have posted from.
        var armed = await db.HnmRecurringBoards.Where(b => b.Enabled).ToListAsync();
        var poller = new HnmRecurringBoardBackgroundService(null!, NullLogger<HnmRecurringBoardBackgroundService>.Instance);
        foreach (var board in armed)
        {
            await poller.ProcessBoardAsync(db, board, Repop.AddMinutes(-3), CancellationToken.None);
        }

        Assert.Empty(await db.Events.ToListAsync());
    }

    // The template is shared per spawn. Another board for it still counts on it.
    [Fact]
    public async Task RemovingOneOfTwoBoardsForTheSpawn_LeavesTheRepeatOn()
    {
        using var db = await SeededAsync(Guid.NewGuid().ToString(), Board(100), Board(101, "Nidhogg"));

        await RemoveAsync(db, 100);

        Assert.True((await db.HnmRecurringBoards.SingleAsync()).Enabled);
    }

    // Staged, not saved: a delete that never commits must not leave the repeat switched off.
    [Fact]
    public async Task TheSwitchOff_IsOnlyCommittedWithTheDelete()
    {
        var name = Guid.NewGuid().ToString();
        using var db = await SeededAsync(name, Board(100));

        var ev = await db.Events.SingleAsync(e => e.Id == 100);
        await HnmRecurringBoardService.StopRepeatingForRemovedBoardAsync(db, ev, CancellationToken.None);

        using var other = NewInMemoryContext(name);
        Assert.True((await other.HnmRecurringBoards.SingleAsync()).Enabled);
    }

    // Not an HNM board: there is no repeat to stop, whatever the template table says.
    [Fact]
    public async Task RemovingANonHnmEvent_LeavesTheRepeatAlone()
    {
        var other = Board(100);
        other.EventType = "Dynamis";
        using var db = await SeededAsync(Guid.NewGuid().ToString(), other);

        await RemoveAsync(db, 100);

        Assert.True((await db.HnmRecurringBoards.SingleAsync()).Enabled);
    }
}
