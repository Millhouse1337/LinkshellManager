using System;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The dashboard card's second tab: which window of its spawn band each HNM pops on.
//
// These pin the decisions that separate it from the Claims donut beside it — unclaimed pops still
// count, the NQ/HQ halves share one row, and the whole configured band is drawn rather than only
// the windows that happened to fire.
public class HnmWindowStatsServiceTests
{
    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static HnmWindowStatsService NewService(ApplicationDbContext db) =>
        new(db, new MonsterTimingResolver(db));

    private static Tod Pop(int id, int linkshellId, string monster, int? window, bool? claim = true) => new()
    {
        Id = id,
        LinkshellId = linkshellId,
        MonsterName = monster,
        PopWindow = window,
        Claim = claim,
        Time = DateTime.UtcNow.AddDays(-1),
    };

    [Fact]
    public async Task CountsPopsPerWindow_AsAShareOfThatMonstersOwnPops()
    {
        using var db = NewInMemoryContext();
        // 3 of 4 Adamantoise pops landed on window 4 — the "pops 75% on window 4" case. The three
        // merge families run the SHORT band (7 × 10 min), so both windows are inside it.
        db.Tods.Add(Pop(1, 10, "Adamantoise", 4));
        db.Tods.Add(Pop(2, 10, "Adamantoise", 4));
        db.Tods.Add(Pop(3, 10, "Adamantoise", 4));
        db.Tods.Add(Pop(4, 10, "Adamantoise", 6));
        await db.SaveChangesAsync();

        var stats = await NewService(db).BuildAsync(10);

        var monster = Assert.Single(stats.Monsters);
        Assert.Equal(4, monster.TotalPops);
        Assert.Equal(7, monster.WindowCount);
        Assert.Equal(4, monster.PeakWindow);
        Assert.Equal(75, monster.PeakPercent);
        Assert.Equal(3, monster.Bars.Single(bar => bar.Window == 4).Count);
        Assert.Equal(25, monster.Bars.Single(bar => bar.Window == 6).Percent);
    }

    [Fact]
    public async Task DrawsTheWholeConfiguredBand_IncludingWindowsThatNeverPopped()
    {
        using var db = NewInMemoryContext();
        db.Tods.Add(Pop(1, 10, "Tiamat", 2));
        await db.SaveChangesAsync();

        var stats = await NewService(db).BuildAsync(10);

        // The wyrms' band is 25 windows, so a single pop on window 2 still yields 25 bars: the
        // empty windows are the finding as much as the busy one. Truncating to the windows that
        // fired would draw a one-bar chart reading "100%" with no context.
        var monster = Assert.Single(stats.Monsters);
        Assert.Equal(25, monster.WindowCount);
        Assert.Equal(25, monster.Bars.Count);
        Assert.Equal(Enumerable.Range(1, 25), monster.Bars.Select(bar => bar.Window));
        Assert.Equal(0, monster.Bars.Single(bar => bar.Window == 17).Count);
    }

    [Fact]
    public async Task MergePairHalvesShareOneRow()
    {
        using var db = NewInMemoryContext();
        // The same spawn logged three ways: the base half, the stronger half, and the combined
        // label an HNM board stores. One grid, so one row — splitting them would halve the
        // sample without answering a different question.
        db.Tods.Add(Pop(1, 10, "Behemoth", 3));
        db.Tods.Add(Pop(2, 10, "King Behemoth", 3));
        db.Tods.Add(Pop(3, 10, "Behemoth/King Behemoth", 5));
        await db.SaveChangesAsync();

        var stats = await NewService(db).BuildAsync(10);

        var monster = Assert.Single(stats.Monsters);
        Assert.Equal("Behemoth/King Behemoth", monster.MonsterName);
        Assert.Equal(3, monster.TotalPops);
        Assert.Equal(3, monster.PeakWindow);
    }

    [Fact]
    public async Task CountsUnclaimedPops_UnlikeTheClaimsDonut()
    {
        using var db = NewInMemoryContext();
        // Which window it spawned in is true whoever killed it. Counting only this linkshell's
        // claims would bias the distribution toward the windows it happens to win.
        db.Tods.Add(Pop(1, 10, "Tiamat", 6, claim: false));
        db.Tods.Add(Pop(2, 10, "Tiamat", 6, claim: null));
        db.Tods.Add(Pop(3, 10, "Tiamat", 8, claim: true));
        await db.SaveChangesAsync();

        var stats = await NewService(db).BuildAsync(10);

        var monster = Assert.Single(stats.Monsters);
        Assert.Equal(3, monster.TotalPops);
        Assert.Equal(6, monster.PeakWindow);
    }

    [Fact]
    public async Task IgnoresPopsWithNoWindow_AndMonstersOutsideTheHnmRoster()
    {
        using var db = NewInMemoryContext();
        db.Tods.Add(Pop(1, 10, "Tiamat", 5));
        db.Tods.Add(Pop(2, 10, "Tiamat", null));       // never recorded a window
        db.Tods.Add(Pop(3, 10, "Genbu", 2));           // a Sky god: no spawn grid to chart
        db.Tods.Add(Pop(4, 10, "Lord of Onzozo", 1));  // a linkshell's custom monster
        db.Tods.Add(Pop(5, 99, "Tiamat", 5));          // another linkshell
        await db.SaveChangesAsync();

        var stats = await NewService(db).BuildAsync(10);

        var monster = Assert.Single(stats.Monsters);
        Assert.Equal("Tiamat", monster.MonsterName);
        Assert.Equal(1, monster.TotalPops);
        Assert.Equal(1, stats.TotalPops);
    }

    [Fact]
    public async Task KeepsPopsRecordedPastAShortenedBand()
    {
        using var db = NewInMemoryContext();
        db.LinkshellMonsterTimings.Add(new LinkshellMonsterTiming
        {
            Id = 1,
            LinkshellId = 10,
            MonsterName = "Tiamat",
            CooldownMinutes = 72 * 60,
            WindowCount = 5,
            WindowCadenceMinutes = 60,
        });
        db.Tods.Add(Pop(1, 10, "Tiamat", 3));
        db.Tods.Add(Pop(2, 10, "Tiamat", 9));
        await db.SaveChangesAsync();

        var stats = await NewService(db).BuildAsync(10);

        // An officer shortening the grid afterwards must not silently drop history recorded under
        // the old one, or the percentages would sum to less than 100.
        var monster = Assert.Single(stats.Monsters);
        Assert.Equal(9, monster.WindowCount);
        Assert.Equal(50, monster.Bars.Single(bar => bar.Window == 9).Percent);
        Assert.Equal(100, monster.Bars.Sum(bar => bar.Percent));
    }

    [Fact]
    public async Task OrdersByMostObserved_AndSharesTheDonutsFamilyColour()
    {
        using var db = NewInMemoryContext();
        db.Tods.Add(Pop(1, 10, "Vrtra", 2));
        db.Tods.Add(Pop(2, 10, "Jormungand", 4));
        db.Tods.Add(Pop(3, 10, "Jormungand", 4));
        await db.SaveChangesAsync();

        var stats = await NewService(db).BuildAsync(10);

        // Most-observed first: a monster with one pop charts "100% on window 2", which is noise
        // and must not lead the card.
        Assert.Equal(new[] { "Jormungand", "Vrtra" }, stats.Monsters.Select(m => m.MonsterName));
        // A monster is the same colour on both tabs of the card.
        Assert.Equal(HnmClaimStatsService.ColorClassFor("Jormungand"), stats.Monsters[0].ColorClass);
        Assert.Equal(HnmClaimStatsService.ColorClassFor("Vrtra"), stats.Monsters[1].ColorClass);
    }

    [Fact]
    public async Task NoLinkshell_OrNoRecordedWindows_IsEmpty()
    {
        using var db = NewInMemoryContext();
        db.Tods.Add(Pop(1, 10, "Tiamat", null));
        await db.SaveChangesAsync();

        Assert.Empty((await NewService(db).BuildAsync(null)).Monsters);
        Assert.Empty((await NewService(db).BuildAsync(10)).Monsters);
        Assert.Equal(0, (await NewService(db).BuildAsync(10)).TotalPops);
    }
}
