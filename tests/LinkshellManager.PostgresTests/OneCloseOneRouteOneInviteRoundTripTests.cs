using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// OneCloseOneRouteOneInvite adds "one route per one-owner post type", "one open invite per person"
// and "one closing window per camp" as unique indexes.
//
// Seeded at the migration before with every kind of duplicate. Up fixes the routes (lowest Id keeps
// the flag), deletes the newer open invites (join requests and answered rows stay), and SKIPS the
// live-camp closing index because one camp has two closes, while still creating everything else.
// Down drops them all. The owner then fixes the camp by hand, an old build writes a new duplicate
// route, and Up again cleans that up and creates every index.
//
// Its OWN database inside the test container, like DropRedundantJobRatingsIndexRoundTripTests.
// Deleted at the end.
[Collection("postgres")]
public class OneCloseOneRouteOneInviteRoundTripTests
{
    private const string Before = "20261114234838_JobRatingsForeignKeys";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string AddIndexes = "20261115003851_OneCloseOneRouteOneInvite";

    private const string OneClosePerEvent = "IX_EventAttendanceWindows_OneClosePerEvent";
    private const string OneClosePerHistory = "IX_EventAttendanceWindows_OneClosePerEventHistory";

    private static readonly string[] RouteAndInviteIndexes =
    {
        "IX_LinkshellChannelRoutes_OneLootRoute",
        "IX_LinkshellChannelRoutes_OneAuctionRoute",
        "IX_LinkshellChannelRoutes_OneTodBoardRoute",
        "IX_LinkshellChannelRoutes_OneDkpSheetRoute",
        "IX_Invites_OneOpenInvite",
        "IX_Invites_OneOpenDiscordInvite",
    };

    private const string Player = "player";
    private const string Other = "other";
    private const string DiscordId = "123456789012345678";

    private readonly PostgresFixture _pg;

    public OneCloseOneRouteOneInviteRoundTripTests(PostgresFixture pg) => _pg = pg;

    private sealed record Seeded(
        int Main, int Second, int[] RouteIds, int SecondRouteId,
        int OldestInvite, int JoinRequest, int DeniedRequest, int OldestDiscordInvite,
        int DoubleCloseCamp, int[] DoubleCloseWindows);

    [Fact]
    public async Task OneCloseOneRouteOneInvite_GoesUpDownAndUpAgain()
    {
        var databaseName = "one_close_route_invite_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(_pg.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");

        var connectionString = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
        {
            Database = databaseName,
            // No pooled connections left behind, so DROP DATABASE at the end is not blocked.
            Pooling = false,
        }.ConnectionString;

        try
        {
            await MigrateAsync(connectionString, Before);
            var seeded = await SeedAsync(connectionString);

            // Up: routes and invites cleaned up and indexed; the live-camp closing index skipped.
            await MigrateAsync(connectionString, AddIndexes);
            await using (var c = await OpenAsync(connectionString))
            {
                foreach (var name in RouteAndInviteIndexes)
                {
                    Assert.True(await IndexExistsAsync(c, name), name);
                }
                Assert.False(await IndexExistsAsync(c, OneClosePerEvent));
                Assert.True(await IndexExistsAsync(c, OneClosePerHistory));

                await AssertRoutesCleanedAsync(c, seeded);
                await AssertInvitesCleanedAsync(c, seeded);

                // The two closes are still both there: money data is never auto-fixed.
                Assert.Equal(2L, await ScalarAsync(c,
                    $"SELECT count(*) FROM \"EventAttendanceWindows\" WHERE \"EventId\" = {seeded.DoubleCloseCamp} AND \"IsClosingWindow\""));
                Assert.Equal(2L, await ScalarAsync(c,
                    $"SELECT count(*) FROM ({OneCloseOneRouteOneInvite.ClosingWindowPreCheckSql}) listed"));

                // Re-running the guarded SQL warns, names the index, and still skips it.
                var notices = new List<string>();
                c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await ScalarAsync(c, OneCloseOneRouteOneInvite.CreateClosingWindowIndexesSql);
                Assert.Contains(notices, n => n.StartsWith($"{OneClosePerEvent} not created", StringComparison.Ordinal));
                Assert.False(await IndexExistsAsync(c, OneClosePerEvent));

                // The clean-ups are safe to run again, and say in the log they did nothing.
                notices.Clear();
                await ScalarAsync(c, OneCloseOneRouteOneInvite.ClearExtraRouteOwnersSql);
                await ScalarAsync(c, OneCloseOneRouteOneInvite.DeleteDuplicateOpenInvitesSql);
                Assert.Contains("OneCloseOneRouteOneInvite: cleared Loot on 0 extra route(s).", notices);
                Assert.Contains("OneCloseOneRouteOneInvite: deleted 0 duplicate open invite(s) by account.", notices);
                Assert.Contains("OneCloseOneRouteOneInvite: deleted 0 duplicate open invite(s) by Discord id.", notices);

                // New duplicates are refused.
                await AssertUniqueViolationAsync(c, RouteSql(seeded.Main, "333", loot: true));
                await AssertUniqueViolationAsync(c, RouteSql(seeded.Main, "333", todBoard: true));
                await AssertUniqueViolationAsync(c, InviteSql(seeded.Main, Player, null, "PendingInvite"));
                await AssertUniqueViolationAsync(c, InviteSql(seeded.Main, null, DiscordId, "PendingInvite"));
                // A join request is not an invite: the player may have both at once. (The seed
                // already has that pair; a second join request is refused by ITS own index.)
                await AssertUniqueViolationAsync(c, InviteSql(seeded.Main, Player, null, "PendingJoinRequest"));
                // Answered rows never count.
                await ScalarAsync(c, InviteSql(seeded.Main, Player, null, "JoinRequestDenied"));
                // Another route, another linkshell, another person: all fine.
                await ScalarAsync(c, RouteSql(seeded.Main, "444", events: true));
                await ScalarAsync(c, InviteSql(seeded.Second, Other, null, "Pending"));
                // A past event's second close is refused.
                await AssertUniqueViolationAsync(c,
                    $"UPDATE \"EventAttendanceWindows\" SET \"IsClosingWindow\" = true " +
                    $"WHERE \"EventHistoryId\" IS NOT NULL AND NOT \"IsClosingWindow\" AND NOT \"IsKillWindow\"");
            }

            // Down: every index goes (the skipped one too, without an error); the data stays.
            await MigrateAsync(connectionString, Before);
            await using (var c = await OpenAsync(connectionString))
            {
                foreach (var name in RouteAndInviteIndexes.Append(OneClosePerEvent).Append(OneClosePerHistory))
                {
                    Assert.False(await IndexExistsAsync(c, name), name);
                }
                await AssertRoutesCleanedAsync(c, seeded);

                // An old build after a rollback can save duplicates again.
                await ScalarAsync(c, RouteSql(seeded.Main, "555", loot: true, todBoardMessageId: "999"));
                await ScalarAsync(c, RouteSql(seeded.Main, "666", todBoard: true, todBoardMessageId: "998"));
                await ScalarAsync(c, InviteSql(seeded.Main, null, DiscordId, "PendingInvite"));

                // The owner fixes the double close by hand: the later window keeps the tick.
                await ScalarAsync(c,
                    $"UPDATE \"EventAttendanceWindows\" SET \"IsClosingWindow\" = false WHERE \"Id\" = {seeded.DoubleCloseWindows[0]}");
            }

            // Up again: the new duplicates are cleaned up and every index is created.
            await MigrateAsync(connectionString, AddIndexes);
            await using (var c = await OpenAsync(connectionString))
            {
                foreach (var name in RouteAndInviteIndexes.Append(OneClosePerEvent).Append(OneClosePerHistory))
                {
                    Assert.True(await IndexExistsAsync(c, name), name);
                }
                await AssertRoutesCleanedAsync(c, seeded);
                Assert.Equal(0L, await ScalarAsync(c,
                    $"SELECT count(*) FROM \"LinkshellChannelRoutes\" WHERE \"ChannelId\" IN ('555', '666') " +
                    $"AND (\"PostLoot\" OR \"PostTodBoard\" OR \"TodBoardMessageId\" IS NOT NULL)"));
                await AssertInvitesCleanedAsync(c, seeded);

                // The close the owner kept is the camp's only one, and a second is now refused.
                Assert.Equal((long)seeded.DoubleCloseWindows[1], Convert.ToInt64(await ScalarAsync(c,
                    $"SELECT \"Id\" FROM \"EventAttendanceWindows\" WHERE \"EventId\" = {seeded.DoubleCloseCamp} AND \"IsClosingWindow\"")));
                await AssertUniqueViolationAsync(c,
                    $"UPDATE \"EventAttendanceWindows\" SET \"IsClosingWindow\" = true WHERE \"Id\" = {seeded.DoubleCloseWindows[0]}");
            }
        }
        finally
        {
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    // Two linkshells and two accounts through EF (the tables have many required columns), then
    // the messy rows.
    private static async Task<Seeded> SeedAsync(string connectionString)
    {
        await using var db = NewContext(connectionString);
        var main = new Linkshell { LinkshellName = "Main LS" };
        var second = new Linkshell { LinkshellName = "Second LS" };
        db.Linkshells.AddRange(main, second);
        db.Users.AddRange(
            new AppUser { Id = Player, UserName = Player },
            new AppUser { Id = Other, UserName = Other });
        await db.SaveChangesAsync();

        // Routes, one save each so the Ids go up in this order:
        //   1: Loot, ToD board (message 900)
        //   2: Loot, ToD board (message 901), DKP sheet (messages)
        //   3: Auctions, DKP sheet (messages)
        //   4: Auctions
        // and the second linkshell's own Loot + ToD board route, which must stay.
        var routes = new[]
        {
            Route(main.Id, "101", loot: true, todBoard: "900"),
            Route(main.Id, "102", loot: true, todBoard: "901", dkpSheet: true),
            Route(main.Id, "103", auctions: true, dkpSheet: true),
            Route(main.Id, "104", auctions: true),
        };
        foreach (var route in routes)
        {
            db.LinkshellChannelRoutes.Add(route);
            await db.SaveChangesAsync();
        }
        var secondRoute = Route(second.Id, "201", loot: true, todBoard: "902");
        db.LinkshellChannelRoutes.Add(secondRoute);
        await db.SaveChangesAsync();

        // Invites in the main linkshell, oldest first: three open invites for the player (old
        // website and Activity spellings), the player's open join request and an answered one,
        // two Discord-only invites for the same Discord user. Plus ones that must stay: the other
        // account's invite, and the same Discord user invited to the second linkshell.
        var invites = new[]
        {
            new Invite { LinkshellId = main.Id, AppUserId = Player, Status = "Pending" },
            new Invite { LinkshellId = main.Id, AppUserId = Player, Status = "PendingInvite" },
            new Invite { LinkshellId = main.Id, AppUserId = Player, Status = "PendingInvite" },
            new Invite { LinkshellId = main.Id, AppUserId = Player, Status = "PendingJoinRequest" },
            new Invite { LinkshellId = main.Id, AppUserId = Player, Status = "JoinRequestDenied" },
            new Invite { LinkshellId = main.Id, DiscordUserId = DiscordId, Status = "PendingInvite" },
            new Invite { LinkshellId = main.Id, DiscordUserId = DiscordId, Status = "PendingInvite" },
            new Invite { LinkshellId = main.Id, AppUserId = Other, Status = "PendingInvite" },
            new Invite { LinkshellId = second.Id, DiscordUserId = DiscordId, Status = "PendingInvite" },
        };
        foreach (var invite in invites)
        {
            db.Invites.Add(invite);
            await db.SaveChangesAsync();
        }

        // Closing windows: one live camp with TWO closes (the guard must skip that index), one
        // live camp whose kill roster is also flagged close next to the real close (kill rows do
        // not count), and a past event with one close and one plain window.
        var doubleClose = new Event { LinkshellId = main.Id, EventName = "Fafnir", EventType = "HNM", StartTime = DateTime.UtcNow };
        var withKill = new Event { LinkshellId = main.Id, EventName = "Adamantoise", EventType = "HNM", StartTime = DateTime.UtcNow };
        var history = new EventHistory { LinkshellId = main.Id, EventName = "Behemoth", EventType = "HNM" };
        db.Events.AddRange(doubleClose, withKill);
        db.EventHistories.Add(history);
        await db.SaveChangesAsync();
        var doubleCloseWindows = new[]
        {
            new EventAttendanceWindow { EventId = doubleClose.Id, SequenceNumber = 1, PostedAt = DateTime.UtcNow, IsClosingWindow = true },
            new EventAttendanceWindow { EventId = doubleClose.Id, SequenceNumber = 2, PostedAt = DateTime.UtcNow, IsClosingWindow = true },
        };
        foreach (var window in doubleCloseWindows)
        {
            db.EventAttendanceWindows.Add(window);
            await db.SaveChangesAsync();
        }
        db.EventAttendanceWindows.AddRange(
            new EventAttendanceWindow { EventId = withKill.Id, SequenceNumber = 1, PostedAt = DateTime.UtcNow, IsClosingWindow = true },
            new EventAttendanceWindow { EventId = withKill.Id, SequenceNumber = 2, PostedAt = DateTime.UtcNow, IsClosingWindow = true, IsKillWindow = true },
            new EventAttendanceWindow { EventHistoryId = history.Id, SequenceNumber = 1, PostedAt = DateTime.UtcNow, IsClosingWindow = true },
            new EventAttendanceWindow { EventHistoryId = history.Id, SequenceNumber = 2, PostedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        return new Seeded(
            main.Id, second.Id, routes.Select(r => r.Id).ToArray(), secondRoute.Id,
            OldestInvite: invites[0].Id, JoinRequest: invites[3].Id, DeniedRequest: invites[4].Id,
            OldestDiscordInvite: invites[5].Id,
            DoubleCloseCamp: doubleClose.Id, DoubleCloseWindows: doubleCloseWindows.Select(w => w.Id).ToArray());
    }

    private static LinkshellChannelRoute Route(
        int linkshellId, string channelId, bool loot = false, string? todBoard = null, bool dkpSheet = false, bool auctions = false) =>
        new()
        {
            LinkshellId = linkshellId,
            ChannelId = channelId,
            PostLoot = loot,
            PostAuctions = auctions,
            PostTodBoard = todBoard is not null,
            TodBoardMessageId = todBoard,
            PostDkpSheet = dkpSheet,
            DkpSheetMessageId = dkpSheet ? "d" + channelId : null,
            DkpSheetExtraMessageIds = dkpSheet ? "e" + channelId : null,
            CreatedAtUtc = DateTime.UtcNow,
        };

    // Route 1 keeps Loot and its board, route 2 keeps only the DKP sheet (it had the lowest Id
    // for that), route 3 loses the sheet and its message ids but keeps Auctions, route 4 loses
    // Auctions. The second linkshell is untouched.
    private static async Task AssertRoutesCleanedAsync(NpgsqlConnection c, Seeded seeded)
    {
        var rows = new Dictionary<int, string>();
        await using (var command = c.CreateCommand())
        {
            command.CommandText =
                "SELECT \"Id\", concat_ws('|', \"PostLoot\"::text, \"PostAuctions\"::text, \"PostTodBoard\"::text, coalesce(\"TodBoardMessageId\", '-'), " +
                "\"PostDkpSheet\"::text, coalesce(\"DkpSheetMessageId\", '-'), coalesce(\"DkpSheetExtraMessageIds\", '-')) " +
                "FROM \"LinkshellChannelRoutes\"";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows[reader.GetInt32(0)] = reader.GetString(1);
            }
        }

        //                                   loot  auct   board  msg  sheet  msg     extra
        Assert.Equal("true|false|true|900|false|-|-", rows[seeded.RouteIds[0]]);
        Assert.Equal("false|false|false|-|true|d102|e102", rows[seeded.RouteIds[1]]);
        Assert.Equal("false|true|false|-|false|-|-", rows[seeded.RouteIds[2]]);
        Assert.Equal("false|false|false|-|false|-|-", rows[seeded.RouteIds[3]]);
        Assert.Equal("true|false|true|902|false|-|-", rows[seeded.SecondRouteId]);
    }

    // The oldest open invite of each person stays; join requests, answered rows, the other
    // account and the other linkshell are untouched.
    private static async Task AssertInvitesCleanedAsync(NpgsqlConnection c, Seeded seeded)
    {
        Assert.Equal(seeded.OldestInvite, await SingleIdAsync(c,
            $"\"LinkshellId\" = {seeded.Main} AND \"AppUserId\" = '{Player}' AND \"Status\" IN ('Pending', 'PendingInvite')"));
        Assert.Equal(seeded.JoinRequest, await SingleIdAsync(c,
            $"\"LinkshellId\" = {seeded.Main} AND \"AppUserId\" = '{Player}' AND \"Status\" = 'PendingJoinRequest'"));
        Assert.Contains(seeded.DeniedRequest, await IdsAsync(c,
            $"\"LinkshellId\" = {seeded.Main} AND \"AppUserId\" = '{Player}' AND \"Status\" = 'JoinRequestDenied'"));
        Assert.Equal(seeded.OldestDiscordInvite, await SingleIdAsync(c,
            $"\"LinkshellId\" = {seeded.Main} AND \"AppUserId\" IS NULL AND \"DiscordUserId\" = '{DiscordId}'"));
        Assert.Single(await IdsAsync(c,
            $"\"LinkshellId\" = {seeded.Main} AND \"AppUserId\" = '{Other}' AND \"Status\" = 'PendingInvite'"));
        Assert.Single(await IdsAsync(c,
            $"\"LinkshellId\" = {seeded.Second} AND \"DiscordUserId\" = '{DiscordId}'"));
    }

    private static async Task<int> SingleIdAsync(NpgsqlConnection c, string where) =>
        Assert.Single(await IdsAsync(c, where));

    private static async Task<List<int>> IdsAsync(NpgsqlConnection c, string where)
    {
        var ids = new List<int>();
        await using var command = c.CreateCommand();
        command.CommandText = $"SELECT \"Id\" FROM \"Invites\" WHERE {where} ORDER BY \"Id\"";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }
        return ids;
    }

    private static string RouteSql(
        int linkshellId, string channelId, bool events = false, bool loot = false, bool todBoard = false, string? todBoardMessageId = null) =>
        "INSERT INTO \"LinkshellChannelRoutes\" (\"LinkshellId\", \"ChannelId\", \"PostEvents\", \"PostLoot\", \"PostAuctions\", " +
        "\"PostTodBoard\", \"PostDkpSheet\", \"TodBoardMessageId\", \"CreatedAtUtc\") " +
        $"VALUES ({linkshellId}, '{channelId}', {Bool(events)}, {Bool(loot)}, false, {Bool(todBoard || todBoardMessageId is not null)}, false, " +
        $"{(todBoardMessageId is null ? "NULL" : $"'{todBoardMessageId}'")}, now())";

    private static string InviteSql(int linkshellId, string? appUserId, string? discordUserId, string status) =>
        "INSERT INTO \"Invites\" (\"LinkshellId\", \"AppUserId\", \"DiscordUserId\", \"Status\") " +
        $"VALUES ({linkshellId}, {(appUserId is null ? "NULL" : $"'{appUserId}'")}, " +
        $"{(discordUserId is null ? "NULL" : $"'{discordUserId}'")}, '{status}')";

    private static string Bool(bool value) => value ? "true" : "false";

    private static async Task AssertUniqueViolationAsync(NpgsqlConnection c, string sql)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c, sql));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlConnection c, string name) =>
        (bool)(await ScalarAsync(c, $"SELECT to_regclass('public.\"{name}\"') IS NOT NULL"))!;

    private static ApplicationDbContext NewContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .UseApplicationServiceProvider(PostgresFixture.IdentityServices)
            .Options;
        return new ApplicationDbContext(options);
    }

    // Moves the schema of the given database to exactly this migration, forwards or backwards.
    private static async Task MigrateAsync(string connectionString, string targetMigration)
    {
        await using var db = NewContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
