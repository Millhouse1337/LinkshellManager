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

// The create-event form's Start pre-fill: "which pops is this linkshell still waiting on?"
//
// The interesting part is not the date filter — it's that ONE spawn must answer as one entry no
// matter which of its spellings the ToD was logged under. An HNM board logs its ToD under the
// board's AssignedMonsterName, which from day 4 is the combined "Fafnir/Nidhogg", while a ToD
// typed on the tracker may say just "Fafnir". A lookup that treated those as different monsters
// would offer two competing start times for the same pop.
public class UpcomingRepopLookupTests
{
    private const int LinkshellId = 11;
    private static readonly DateTime Now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Tod NewTod(int id, string monster, double repopInHours, int linkshellId = LinkshellId)
        => new()
        {
            Id = id,
            LinkshellId = linkshellId,
            MonsterName = monster,
            Time = Now.AddHours(repopInHours - 22),
            RepopTime = Now.AddHours(repopInHours),
            DayNumber = 3
        };

    private static Task<System.Collections.Generic.List<UpcomingRepopLookup.Entry>> LookupAsync(ApplicationDbContext db)
        => UpcomingRepopLookup.ForLinkshellAsync(db, LinkshellId, Now, CancellationToken.None);

    [Fact]
    public async Task Returns_only_pops_that_have_not_happened_yet()
    {
        using var db = NewInMemoryContext();
        db.Tods.AddRange(
            NewTod(1, "Fafnir", repopInHours: 5),
            NewTod(2, "Behemoth", repopInHours: -1));
        await db.SaveChangesAsync();

        var entries = await LookupAsync(db);

        Assert.Equal(new[] { "Fafnir" }, entries.Select(entry => entry.MonsterName));
    }

    [Fact]
    public async Task Ignores_other_linkshells()
    {
        using var db = NewInMemoryContext();
        db.Tods.AddRange(
            NewTod(1, "Fafnir", repopInHours: 5, linkshellId: LinkshellId + 1),
            NewTod(2, "Vrtra", repopInHours: 9));
        await db.SaveChangesAsync();

        var entries = await LookupAsync(db);

        Assert.Equal(new[] { "Vrtra" }, entries.Select(entry => entry.MonsterName));
    }

    [Fact]
    public async Task Soonest_pop_comes_first()
    {
        using var db = NewInMemoryContext();
        db.Tods.AddRange(
            NewTod(1, "Vrtra", repopInHours: 30),
            NewTod(2, "Tiamat", repopInHours: 3));
        await db.SaveChangesAsync();

        var entries = await LookupAsync(db);

        Assert.Equal(new[] { "Tiamat", "Vrtra" }, entries.Select(entry => entry.MonsterName));
    }

    [Fact]
    public async Task One_entry_per_spawn_even_across_merge_pair_spellings()
    {
        using var db = NewInMemoryContext();
        db.Tods.AddRange(
            NewTod(1, "Fafnir", repopInHours: 20),
            NewTod(2, "Fafnir/Nidhogg", repopInHours: 26));
        await db.SaveChangesAsync();

        var entries = await LookupAsync(db);

        // The NEWEST row wins — the same row the recurring-board poller acts on — even though the
        // older one predicts a sooner pop.
        var entry = Assert.Single(entries);
        Assert.Equal("Fafnir/Nidhogg", entry.MonsterName);
        Assert.Equal(Now.AddHours(26), entry.RepopTimeUtc);
    }

    [Fact]
    public async Task Match_names_cover_every_spelling_of_the_spawn()
    {
        using var db = NewInMemoryContext();
        db.Tods.Add(NewTod(1, "Fafnir", repopInHours: 8));
        await db.SaveChangesAsync();

        var entry = Assert.Single(await LookupAsync(db));

        // A camp picked as any of these is waiting on this ToD's pop.
        Assert.Contains("Fafnir", entry.MatchNames);
        Assert.Contains("Nidhogg", entry.MatchNames);
        Assert.Contains("Fafnir/Nidhogg", entry.MatchNames);
    }

    [Fact]
    public async Task Repop_time_is_stamped_as_utc()
    {
        using var db = NewInMemoryContext();
        db.Tods.Add(NewTod(1, "Behemoth", repopInHours: 4));
        await db.SaveChangesAsync();

        var entry = Assert.Single(await LookupAsync(db));

        // Tod.RepopTime round-trips as Unspecified; the caller serializes it straight to the
        // client, so an unstamped Kind would be read as a local time and shift the pre-fill.
        Assert.Equal(DateTimeKind.Utc, entry.RepopTimeUtc.Kind);
    }
}
