using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace LinkshellManager.Tests;

// Two officers pressing Post on the same not-yet-posted window at the same moment. Both find no row
// and both try to create it; the unique (EventId, SequenceNumber) index rejects the second. That post
// used to fail with a bare 500 -- it now joins the window the first one created.
//
// The in-memory provider enforces no unique index, so the race is staged: on the first save, a second
// context writes the "other officer's" window and the save then fails the way Postgres fails it.
public class AttendanceWindowCreateRaceTests
{
    private const int EventId = 500;
    private const int Sequence = 3;

    private static DbContextOptions<ApplicationDbContext> Options(string name, params IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .AddInterceptors(interceptors)
            .Options;

    private static async Task<string> SeededDatabaseAsync()
    {
        var name = Guid.NewGuid().ToString();
        await using var seed = new ApplicationDbContext(Options(name));
        seed.Events.Add(new Event { Id = EventId, LinkshellId = 1, EventName = "faf", EventType = "HNM" });
        await seed.SaveChangesAsync();
        return name;
    }

    private static EventAttendanceWindow Window(string postedBy) => new()
    {
        EventId = EventId,
        SequenceNumber = Sequence,
        Label = "Window 3",
        PostedAt = DateTime.UtcNow,
        PostedBySource = postedBy,
    };

    private static DbUpdateException Rejection(string sqlState) =>
        new("save failed", new PostgresException("rejected", "ERROR", "ERROR", sqlState));

    // Fails the FIRST save only, after running `beforeFailing` -- the other officer's post landing
    // in between our "no row yet" read and our insert.
    private sealed class FailFirstSave(Func<Task> beforeFailing, DbUpdateException failure) : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_fired) return result;
            _fired = true;
            await beforeFailing();
            throw failure;
        }
    }

    [Fact]
    public async Task NoRace_SavesAndReturnsTheNewWindow()
    {
        var name = await SeededDatabaseAsync();
        await using var db = new ApplicationDbContext(Options(name));

        var ours = Window("officer A");
        var saved = await AddonApiController.SaveNewWindowOrAdoptExistingAsync(db, ours, CancellationToken.None);

        Assert.Same(ours, saved);
        Assert.Equal(1, await db.EventAttendanceWindows.CountAsync());
    }

    [Fact]
    public async Task LostRace_JoinsTheWindowTheOtherPostCreated()
    {
        var name = await SeededDatabaseAsync();
        var otherOfficer = async () =>
        {
            await using var other = new ApplicationDbContext(Options(name));
            other.EventAttendanceWindows.Add(Window("officer B"));
            await other.SaveChangesAsync();
        };
        await using var db = new ApplicationDbContext(
            Options(name, new FailFirstSave(otherOfficer, Rejection(PostgresErrorCodes.UniqueViolation))));

        // Pending alongside the insert, like the addon's auto-commence: it must survive the failed
        // save and go out with the next one.
        var camp = await db.Events.SingleAsync(e => e.Id == EventId);
        var commencedAt = new DateTime(2026, 11, 4, 3, 0, 0, DateTimeKind.Utc);
        camp.CommencementStartTime = commencedAt;

        var ours = Window("officer A");
        var saved = await AddonApiController.SaveNewWindowOrAdoptExistingAsync(db, ours, CancellationToken.None);

        Assert.NotSame(ours, saved);
        Assert.Equal("officer B", saved.PostedBySource);
        Assert.Equal(EntityState.Detached, db.Entry(ours).State);

        // The next save on this request must not retry our insert.
        await db.SaveChangesAsync();
        await using var check = new ApplicationDbContext(Options(name));
        var windows = await check.EventAttendanceWindows.ToListAsync();
        Assert.Equal("officer B", Assert.Single(windows).PostedBySource);
        Assert.Equal(commencedAt, (await check.Events.SingleAsync()).CommencementStartTime);
    }

    [Fact]
    public async Task UniqueRejectionWithNoWinningRow_IsRethrown()
    {
        var name = await SeededDatabaseAsync();
        await using var db = new ApplicationDbContext(
            Options(name, new FailFirstSave(() => Task.CompletedTask, Rejection(PostgresErrorCodes.UniqueViolation))));

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddonApiController.SaveNewWindowOrAdoptExistingAsync(db, Window("officer A"), CancellationToken.None));
    }

    [Fact]
    public async Task OtherSaveFailures_AreRethrown()
    {
        var name = await SeededDatabaseAsync();
        await using var db = new ApplicationDbContext(
            Options(name, new FailFirstSave(() => Task.CompletedTask, Rejection(PostgresErrorCodes.ForeignKeyViolation))));

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddonApiController.SaveNewWindowOrAdoptExistingAsync(db, Window("officer A"), CancellationToken.None));
    }
}
