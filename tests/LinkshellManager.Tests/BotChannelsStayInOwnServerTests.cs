using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The bot posts wherever a linkshell's settings point it. A channel id is just a number, and the
// bot can reach channels in every server it is in, so these settings must stay inside the
// linkshell's OWN Discord server: routes and the discussion channel only accept channels the bot
// lists for that server, and a new server is only set after checking the caller is in it.
public class BotChannelsStayInOwnServerTests
{
    private const int LinkshellId = 7;

    private static readonly IReadOnlyList<DiscordChannelInfo> OwnServerChannels = new[]
    {
        new DiscordChannelInfo("111", "events", 0),
        new DiscordChannelInfo("222", "loot", 1),
    };

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ChannelRouteEditor NewEditor(ApplicationDbContext db) =>
        new(db, new DiscordTodBoardQueue(), new DkpSheetPostQueue(), new MonsterTimingResolver(db));

    private static ChannelRouteEdit Route(string channelId, int? id = null) =>
        new(id, null, channelId, PostEvents: true, PostLoot: false, PostAuctions: false,
            PostTodBoard: false, PostDkpSheet: false, EventTypeFilter: null);

    [Fact]
    public async Task Route_to_a_channel_from_another_server_is_rejected_and_nothing_saved()
    {
        using var db = NewDb();

        var error = await NewEditor(db).SaveAsync(
            LinkshellId, new[] { Route("111"), Route("999") }, OwnServerChannels, CancellationToken.None);

        Assert.NotNull(error);
        Assert.Empty(db.LinkshellChannelRoutes);
    }

    [Fact]
    public async Task Route_to_a_channel_in_the_own_server_is_saved()
    {
        using var db = NewDb();

        var error = await NewEditor(db).SaveAsync(
            LinkshellId, new[] { Route("222") }, OwnServerChannels, CancellationToken.None);

        Assert.Null(error);
        var route = Assert.Single(db.LinkshellChannelRoutes);
        Assert.Equal("222", route.ChannelId);
        Assert.Equal("loot", route.ChannelName);
    }

    // No server set (or the bot can't see it): there is nothing to check against, so no route
    // may be saved rather than trusting whatever id was typed.
    [Fact]
    public async Task Routes_are_rejected_when_the_server_channels_are_unknown()
    {
        using var db = NewDb();

        var error = await NewEditor(db).SaveAsync(
            LinkshellId, new[] { Route("111") }, null, CancellationToken.None);

        Assert.NotNull(error);
        Assert.Empty(db.LinkshellChannelRoutes);
    }

    // Removing routes never sends the bot anywhere, so it works even with no channel list. This
    // is how an officer cleans up routes left pointing at an old server.
    [Fact]
    public async Task Removing_every_route_works_without_a_channel_list()
    {
        using var db = NewDb();
        db.LinkshellChannelRoutes.Add(new LinkshellChannelRoute
        {
            Id = 5, LinkshellId = LinkshellId, ChannelId = "999", PostEvents = true,
        });
        await db.SaveChangesAsync();

        var error = await NewEditor(db).SaveAsync(
            LinkshellId, Array.Empty<ChannelRouteEdit>(), null, CancellationToken.None);

        Assert.Null(error);
        Assert.Empty(db.LinkshellChannelRoutes);
    }

    // The discussion-channel setter uses the same check with a single id.
    [Theory]
    [InlineData("111", true)]
    [InlineData(" 222 ", true)]
    [InlineData("999", false)]
    public void Discussion_channel_must_be_in_the_own_server(string channelId, bool allowed)
    {
        var error = ChannelRouteEditor.CheckChannelsInGuild(new[] { channelId }, OwnServerChannels);
        Assert.Equal(allowed, error is null);
    }

    [Fact]
    public void Discussion_channel_is_rejected_when_the_server_channels_are_unknown()
    {
        Assert.NotNull(ChannelRouteEditor.CheckChannelsInGuild(new[] { "111" }, null));
    }

    // set-guild: the dropdown pick wins; the Activity's server (header) is only a fallback. Either
    // way the controller then checks the caller is in that server — the header is client-sent.
    [Theory]
    [InlineData("123", "456", "123")]
    [InlineData(null, "456", "456")]
    [InlineData("  ", " 456 ", "456")]
    [InlineData(null, null, null)]
    [InlineData("", "", null)]
    public void Set_guild_picks_the_dropdown_then_the_launch_server(string? picked, string? header, string? expected)
    {
        Assert.Equal(expected, ActivitySettingsController.PickGuildToSet(picked, header));
    }

    // /update: only a NEW server id needs the membership check; echoing the stored id back or
    // leaving it out must not change anything.
    [Theory]
    [InlineData(null, "123", "Unchanged")]
    [InlineData("123", "123", "Unchanged")]
    [InlineData(" 123 ", "123", "Unchanged")]
    [InlineData("", "123", "Clear")]
    [InlineData("456", "123", "Set")]
    [InlineData("456", null, "Set")]
    [InlineData("not-a-server", "123", "Invalid")]
    [InlineData("123456789012345678901", null, "Invalid")]
    public void Update_only_verifies_a_new_server(
        string? requested, string? current, string expected)
    {
        Assert.Equal(expected, ActivitySettingsController.ClassifyGuildIdUpdate(requested, current).ToString());
    }
}
