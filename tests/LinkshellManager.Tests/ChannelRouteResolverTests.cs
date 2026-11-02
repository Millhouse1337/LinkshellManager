using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Which channel an event posts to. Set up like a real linkshell: #events takes every type but
// HNM, #tiamat takes HNM for Tiamat only. An HNM camp with no route of its own used to fall back
// to the "Other" route, so a Cerberus board landed in #events with HNM unticked there.
public class ChannelRouteResolverTests
{
    private static ChannelRouteResolver Setup()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.LinkshellChannelRoutes.AddRange(
            new LinkshellChannelRoute
            {
                Id = 1, LinkshellId = 1, ChannelId = "events", PostEvents = true,
                EventTypeFilter = "Sky|Sea|HENM|Limbus|Dynamis|BCNM|KSNM|Other",
            },
            new LinkshellChannelRoute
            {
                Id = 2, LinkshellId = 1, ChannelId = "tiamat", PostEvents = true,
                EventTypeFilter = "HNM", HnmMonsterFilter = "Tiamat",
            });
        db.SaveChanges();
        return new ChannelRouteResolver(db);
    }

    [Fact]
    public async Task Hnm_with_no_route_of_its_own_posts_nowhere()
    {
        var channel = await Setup().ResolveEventChannelIdAsync(1, "HNM", "Cerberus", CancellationToken.None);
        Assert.Null(channel);
    }

    [Fact]
    public async Task Hnm_goes_to_its_monster_route()
    {
        var channel = await Setup().ResolveEventChannelIdAsync(1, "HNM", "Tiamat", CancellationToken.None);
        Assert.Equal("tiamat", channel);
    }

    [Fact]
    public async Task Hnm_with_no_monster_skips_a_monster_only_route()
    {
        var channel = await Setup().ResolveEventChannelIdAsync(1, "HNM", null, CancellationToken.None);
        Assert.Null(channel);
    }

    [Fact]
    public async Task Other_types_still_reach_the_other_route()
    {
        var resolver = Setup();
        Assert.Equal("events", await resolver.ResolveEventChannelIdAsync(1, "Dynamis", null, CancellationToken.None));
        Assert.Equal("events", await resolver.ResolveEventChannelIdAsync(1, "Farming", null, CancellationToken.None));
    }
}
