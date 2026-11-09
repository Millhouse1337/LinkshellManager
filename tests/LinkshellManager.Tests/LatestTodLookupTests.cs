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

// The create-event form's "Last ToD" readout: the newest ToD per spawn, whether or not its repop
// has passed, matched across merge-pair spellings like UpcomingRepopLookup.
public class LatestTodLookupTests
{
    private const int LinkshellId = 11;
    private static readonly DateTime Now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Tod NewTod(int id, string monster, double hoursAgo, int linkshellId = LinkshellId)
        => new()
        {
            Id = id,
            LinkshellId = linkshellId,
            MonsterName = monster,
            Time = Now.AddHours(-hoursAgo),
            TimeStamp = Now.AddHours(-hoursAgo),
            // Long past: the upcoming-repops list would leave every one of these out.
            RepopTime = Now.AddHours(-hoursAgo + 1)
        };

    private static Task<System.Collections.Generic.List<LatestTodLookup.Entry>> LookupAsync(ApplicationDbContext db)
        => LatestTodLookup.ForLinkshellAsync(db, LinkshellId, CancellationToken.None);

    [Fact]
    public async Task Answers_a_monster_whose_repop_has_already_passed()
    {
        using var db = NewInMemoryContext();
        db.Tods.Add(NewTod(1, "Behemoth", hoursAgo: 72));
        await db.SaveChangesAsync();

        var entry = Assert.Single(await LookupAsync(db));

        Assert.Equal(Now.AddHours(-72), entry.TimeUtc);
        Assert.Equal(DateTimeKind.Utc, entry.TimeUtc!.Value.Kind);
    }

    [Fact]
    public async Task Newest_kill_wins_across_merge_pair_spellings()
    {
        using var db = NewInMemoryContext();
        db.Tods.AddRange(
            NewTod(1, "Fafnir", hoursAgo: 5),
            NewTod(2, "Fafnir/Nidhogg", hoursAgo: 30),
            NewTod(3, "Vrtra", hoursAgo: 2, linkshellId: LinkshellId + 1));
        await db.SaveChangesAsync();

        var entry = Assert.Single(await LookupAsync(db));

        Assert.Equal(1, entry.TodId);
        Assert.Contains("Fafnir/Nidhogg", entry.MatchNames);
    }

    // The Day box on the event form is filled from NextDay: an NQ kill moves the cycle on a day,
    // an HQ starts it over at 1 -- including an HQ posted by name ("Nidhogg") with no HQ toggle.
    [Theory]
    [InlineData("Fafnir", 1, false, 2)]
    [InlineData("Fafnir/Nidhogg", 5, false, 6)]
    [InlineData("Fafnir/Nidhogg", 5, true, 1)]
    [InlineData("Nidhogg", 6, false, 1)]
    [InlineData("Fafnir", null, false, null)]
    public async Task Next_day_follows_the_day_cycle(string monster, int? day, bool hq, int? expectedNextDay)
    {
        using var db = NewInMemoryContext();
        var tod = NewTod(1, monster, hoursAgo: 3);
        tod.DayNumber = day;
        tod.Hq = hq;
        db.Tods.Add(tod);
        await db.SaveChangesAsync();

        var entry = Assert.Single(await LookupAsync(db));

        Assert.Equal(day, entry.DayNumber);
        Assert.Equal(expectedNextDay, entry.NextDay);
    }

    [Fact]
    public async Task A_not_entered_tod_still_counts_as_the_latest()
    {
        using var db = NewInMemoryContext();
        db.Tods.AddRange(
            NewTod(1, "Tiamat", hoursAgo: 80),
            new Tod { Id = 2, LinkshellId = LinkshellId, MonsterName = "Tiamat", TimeStamp = Now.AddHours(-1) });
        await db.SaveChangesAsync();

        var entry = Assert.Single(await LookupAsync(db));

        Assert.Equal(2, entry.TodId);
        Assert.Null(entry.TimeUtc);
    }
}
