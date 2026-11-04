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

// Which auction cards get redrawn because time passed: an auction reaching its end (Live -> Ended)
// or its scheduled start (Queued -> Live). Nothing is saved at those moments, so nothing else redraws
// them -- a card used to keep saying "Live · ends 18 hours ago" with its bid picker still on it.
public class AuctionCardTimerTests
{
    private static readonly DateTime Now = new(2026, 11, 4, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LastCheck = Now.AddSeconds(-30);

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Auction Posted(int id, DateTime start, DateTime end, DateTime? startedAt = null) => new()
    {
        Id = id,
        LinkshellId = 1,
        AuctionTitle = $"auction {id}",
        StartTime = start,
        EndTime = end,
        StartedAt = startedAt,
        DiscordChannelId = "100",
        DiscordMessageId = $"msg{id}",
    };

    private static async Task<ApplicationDbContext> SeededAsync(params Auction[] auctions)
    {
        var db = NewDb();
        db.Auctions.AddRange(auctions);
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task FirstPass_RedrawsEveryPostedCardWhoseStartOrEndIsBehindIt()
    {
        var unposted = Posted(5, Now.AddDays(-2), Now.AddDays(-1));
        unposted.DiscordMessageId = null;
        await using var db = await SeededAsync(
            Posted(1, Now.AddDays(-3), Now.AddDays(-1)),     // ended a day ago, card still says Live
            Posted(2, Now.AddHours(-1), Now.AddDays(2)),     // started on the clock while the app was down
            Posted(3, Now.AddHours(1), Now.AddDays(2)),      // still queued: nothing to change
            Posted(4, Now.AddHours(1), Now.AddDays(2), startedAt: Now.AddMinutes(-5)), // started early, live
            unposted);                                       // never posted: no card to redraw

        var due = await AuctionCardTimerBackgroundService.DueForRedrawAsync(db, null, Now, CancellationToken.None);

        Assert.Equal(new[] { 1, 2 }, due.OrderBy(id => id));
    }

    [Fact]
    public async Task LaterPass_RedrawsOnlyCardsThatCrossedABoundarySinceTheLastCheck()
    {
        await using var db = await SeededAsync(
            Posted(1, Now.AddDays(-2), Now.AddSeconds(-10)),  // just ended
            Posted(2, Now.AddSeconds(-10), Now.AddDays(2)),   // just reached its scheduled start
            Posted(3, Now.AddDays(-2), Now.AddMinutes(-5)),   // ended before the last check: already redrawn
            Posted(4, Now.AddDays(-1), Now.AddDays(2)),       // live, nothing changed
            Posted(5, Now.AddSeconds(-10), Now.AddDays(2), startedAt: Now.AddMinutes(-20))); // Start already redrew it

        var due = await AuctionCardTimerBackgroundService.DueForRedrawAsync(db, LastCheck, Now, CancellationToken.None);

        Assert.Equal(new[] { 1, 2 }, due.OrderBy(id => id));
    }
}
