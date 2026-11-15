using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// A later migration adds "at most one closing window per camp" as a unique index, which Postgres
// checks row by row. ClosingWindowMark moves the tick in two saves so the move is never refused.
// The index is built by hand inside a transaction that is rolled back, so the shared database
// stays as the migrations made it.
[Collection("postgres")]
public class ClosingWindowMoveTests : IAsyncLifetime
{
    private const string OneClosePerEventSql =
        "CREATE UNIQUE INDEX \"IX_Test_OneClosePerEvent\" ON \"EventAttendanceWindows\" (\"EventId\") " +
        "WHERE \"IsClosingWindow\" AND NOT \"IsKillWindow\"";

    private readonly PostgresFixture _pg;

    public ClosingWindowMoveTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // A live camp with windows 1-3, window 3 ticked as the close, and a second camp whose window 1
    // is ticked (it must never be touched).
    private async Task<(int Camp, int OtherCamp, int[] WindowIds, int OtherWindowId)> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "Close LS" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();

        var camp = new Event { LinkshellId = linkshell.Id, EventName = "Fafnir", EventType = "HNM", StartTime = DateTime.UtcNow };
        var otherCamp = new Event { LinkshellId = linkshell.Id, EventName = "Adamantoise", EventType = "HNM", StartTime = DateTime.UtcNow };
        db.Events.AddRange(camp, otherCamp);
        await db.SaveChangesAsync();

        var windows = Enumerable.Range(1, 3)
            .Select(sequence => new EventAttendanceWindow
            {
                EventId = camp.Id, SequenceNumber = sequence, PostedAt = DateTime.UtcNow, IsClosingWindow = sequence == 3,
            })
            .ToList();
        var otherWindow = new EventAttendanceWindow
        {
            EventId = otherCamp.Id, SequenceNumber = 1, PostedAt = DateTime.UtcNow, IsClosingWindow = true,
        };
        foreach (var window in windows)
        {
            db.EventAttendanceWindows.Add(window);
            await db.SaveChangesAsync();
        }
        db.EventAttendanceWindows.Add(otherWindow);
        await db.SaveChangesAsync();
        return (camp.Id, otherCamp.Id, windows.Select(w => w.Id).ToArray(), otherWindow.Id);
    }

    [Fact]
    public async Task The_close_moves_to_a_lower_id_window()
    {
        var (camp, _, windowIds, otherWindowId) = await SeedAsync();

        await using var db = _pg.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(OneClosePerEventSql);

        var windows = await ClosingWindowMark.MoveAsync(db, camp, windowIds[0], CancellationToken.None);

        Assert.Equal(3, windows.Count);
        db.ChangeTracker.Clear();
        var closing = await db.EventAttendanceWindows.Where(w => w.IsClosingWindow).Select(w => w.Id).OrderBy(id => id).ToListAsync();
        Assert.Equal(new[] { windowIds[0], otherWindowId }.OrderBy(id => id), closing);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Unticking_clears_only_this_camp()
    {
        var (camp, _, _, otherWindowId) = await SeedAsync();

        await using var db = _pg.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(OneClosePerEventSql);

        await ClosingWindowMark.MoveAsync(db, camp, null, CancellationToken.None);

        db.ChangeTracker.Clear();
        var closing = await db.EventAttendanceWindows.Where(w => w.IsClosingWindow).Select(w => w.Id).ToListAsync();
        Assert.Equal(new[] { otherWindowId }, closing);

        await transaction.RollbackAsync();
    }

    // Why it saves twice: the same move as ONE plain save is refused by such an index.
    [Fact]
    public async Task One_plain_save_of_the_same_move_is_refused()
    {
        var (camp, _, windowIds, _) = await SeedAsync();

        await using var db = _pg.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(OneClosePerEventSql);

        var windows = await db.EventAttendanceWindows.Where(w => w.EventId == camp).ToListAsync();
        foreach (var window in windows)
        {
            window.IsClosingWindow = window.Id == windowIds[0];
        }

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(refused.InnerException).SqlState);

        await transaction.RollbackAsync();
    }

    // Without a caller's transaction the helper opens and commits its own.
    [Fact]
    public async Task On_its_own_it_commits_both_saves()
    {
        var (camp, _, windowIds, _) = await SeedAsync();

        await using (var db = _pg.CreateContext())
        {
            await ClosingWindowMark.MoveAsync(db, camp, windowIds[1], CancellationToken.None);
            Assert.Null(db.Database.CurrentTransaction);
        }

        await using var check = _pg.CreateContext();
        var closing = await check.EventAttendanceWindows
            .Where(w => w.EventId == camp && w.IsClosingWindow).Select(w => w.Id).ToListAsync();
        Assert.Equal(new[] { windowIds[1] }, closing);
    }
}
