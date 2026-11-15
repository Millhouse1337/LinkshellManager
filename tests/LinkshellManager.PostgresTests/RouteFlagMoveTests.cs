using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// OneCloseOneRouteOneInvite adds "one route per linkshell posts Loot" (and the same for Auctions,
// the ToD board and the DKP sheet) as unique indexes. Postgres checks those row by row, so moving
// Loot from one route to another in a single save fails for a moment where both routes hold it.
// ChannelRouteEditor saves in two steps to avoid that. These tests run against the real indexes
// (each test first checks they are there), inside a transaction that is rolled back.
[Collection("postgres")]
public class RouteFlagMoveTests : IAsyncLifetime
{
    private const string OneLootRoute = "IX_LinkshellChannelRoutes_OneLootRoute";
    private const string OneTodBoardRoute = "IX_LinkshellChannelRoutes_OneTodBoardRoute";

    private static readonly IReadOnlyList<DiscordChannelInfo> Channels = new[]
    {
        new DiscordChannelInfo("111", "events", 0),
        new DiscordChannelInfo("222", "loot", 1),
    };

    private readonly PostgresFixture _pg;

    public RouteFlagMoveTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // Route 1 posts events, route 2 posts Loot and the ToD board (with a live board message).
    private async Task<(int LinkshellId, int First, int Second)> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "Route LS" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        var first = new LinkshellChannelRoute { LinkshellId = linkshell.Id, ChannelId = "111", PostEvents = true };
        var second = new LinkshellChannelRoute
        {
            LinkshellId = linkshell.Id, ChannelId = "222", PostLoot = true, PostTodBoard = true, TodBoardMessageId = "900",
        };
        db.LinkshellChannelRoutes.Add(first);
        await db.SaveChangesAsync();
        db.LinkshellChannelRoutes.Add(second);
        await db.SaveChangesAsync();
        return (linkshell.Id, first.Id, second.Id);
    }

    private static ChannelRouteEditor Editor(LinkshellManagerDiscordApp.Data.ApplicationDbContext db) =>
        new(db, new DiscordTodBoardQueue(), new DkpSheetPostQueue(), new MonsterTimingResolver(db));

    private static ChannelRouteEdit Edit(int id, string channelId, bool events, bool loot, bool todBoard) =>
        new(id, null, channelId, PostEvents: events, PostLoot: loot, PostAuctions: false,
            PostTodBoard: todBoard, PostDkpSheet: false, EventTypeFilter: null);

    [Fact]
    public async Task Loot_moves_to_a_lower_id_route_in_one_save()
    {
        var (linkshellId, first, second) = await SeedAsync();

        await using var db = _pg.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await PostgresFixture.AssertIndexExistsAsync(db, OneLootRoute);
        await PostgresFixture.AssertIndexExistsAsync(db, OneTodBoardRoute);

        // Loot moves from route 2 to route 1; route 2 keeps the ToD board in the same channel.
        var error = await Editor(db).SaveAsync(
            linkshellId,
            new[]
            {
                Edit(first, "111", events: true, loot: true, todBoard: false),
                Edit(second, "222", events: false, loot: false, todBoard: true),
            },
            Channels,
            CancellationToken.None);

        Assert.Null(error);
        db.ChangeTracker.Clear();
        var routes = await db.LinkshellChannelRoutes.OrderBy(r => r.Id).ToListAsync();
        Assert.True(routes[0].PostLoot);
        Assert.False(routes[1].PostLoot);
        Assert.True(routes[1].PostTodBoard);
        // Clearing the flag for a moment did not cost the live board its message.
        Assert.Equal("900", routes[1].TodBoardMessageId);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task The_ToD_board_moves_and_its_old_message_is_forgotten()
    {
        var (linkshellId, first, second) = await SeedAsync();

        await using var db = _pg.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await PostgresFixture.AssertIndexExistsAsync(db, OneTodBoardRoute);

        var error = await Editor(db).SaveAsync(
            linkshellId,
            new[]
            {
                Edit(first, "111", events: true, loot: false, todBoard: true),
                Edit(second, "222", events: false, loot: true, todBoard: false),
            },
            Channels,
            CancellationToken.None);

        Assert.Null(error);
        db.ChangeTracker.Clear();
        var routes = await db.LinkshellChannelRoutes.OrderBy(r => r.Id).ToListAsync();
        Assert.True(routes[0].PostTodBoard);
        Assert.Null(routes[0].TodBoardMessageId);
        Assert.False(routes[1].PostTodBoard);
        Assert.Null(routes[1].TodBoardMessageId);

        await transaction.RollbackAsync();
    }

    // Why the editor saves twice: the same move as ONE plain save is refused by such an index.
    [Fact]
    public async Task One_plain_save_of_the_same_move_is_refused()
    {
        var (_, first, second) = await SeedAsync();

        await using var db = _pg.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await PostgresFixture.AssertIndexExistsAsync(db, OneLootRoute);

        var routes = await db.LinkshellChannelRoutes.Where(r => r.Id == first || r.Id == second).ToListAsync();
        routes.Single(r => r.Id == first).PostLoot = true;
        routes.Single(r => r.Id == second).PostLoot = false;

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(refused.InnerException).SqlState);

        await transaction.RollbackAsync();
    }
}
