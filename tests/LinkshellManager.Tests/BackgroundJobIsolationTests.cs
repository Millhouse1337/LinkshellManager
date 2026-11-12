using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// P3.9 / backend-08: one bad row must never stall a background job for everyone else.
//
// The three timed jobs -- re-posting standing HNM boards, auto-starting due events, and marching live
// camps through their windows -- each walked every row on ONE DbContext. So one row whose save failed
// either stopped the shared save (no camp in any linkshell went live or advanced), or left its
// half-made changes in the shared tracker, where the next row's save tried to write them again and
// failed too. Now each row runs on its own context: the bad one is logged and skipped, and the rest
// of the tick carries on.
//
// Each test poisons the FIRST row's save (the jobs walk rows in id order) and checks the second row
// still went through, and that nothing of the first one was saved.
public class BackgroundJobIsolationTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc);

    private static DbContextOptions<ApplicationDbContext> NewOptions(
        InMemoryDatabaseRoot root, string name, params IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name, root)
            .AddInterceptors(interceptors)
            .Options;

    private static ApplicationDbContext NewDb(InMemoryDatabaseRoot root, string name) =>
        new(NewOptions(root, name));

    // A brand new context per scope, on the shared in-memory database -- what production's DI does.
    private sealed class FreshContextScopes(DbContextOptions<ApplicationDbContext> options) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(new ApplicationDbContext(options));

        private sealed class Scope(ApplicationDbContext db) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) =>
                serviceType == typeof(ApplicationDbContext) ? db : null;
            public void Dispose() => db.Dispose();
        }
    }

    // Fails any save that would write the poisoned row -- a stand-in for whatever bad data or broken
    // rule makes one row's save throw in production.
    private sealed class FailSavesTouching(Func<object, bool> poisoned) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var touches = eventData.Context!.ChangeTracker.Entries()
                .Any(entry => entry.State is EntityState.Added or EntityState.Modified && poisoned(entry.Entity));
            if (touches)
            {
                throw new InvalidOperationException("This row's save is poisoned.");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static (InMemoryDatabaseRoot Root, string Name, IServiceScopeFactory Scopes) NewJobDatabase(
        Func<object, bool> poisoned)
    {
        var root = new InMemoryDatabaseRoot();
        var name = Guid.NewGuid().ToString();
        var scopes = new FreshContextScopes(NewOptions(root, name, new FailSavesTouching(poisoned)));
        return (root, name, scopes);
    }

    [Fact]
    public async Task RecurringBoards_AFailingBoard_DoesNotStopTheNext_AndItsChangesAreNotSavedWithIt()
    {
        var (root, name, scopes) = NewJobDatabase(entity => entity is HnmRecurringBoard { Id: 1 });
        var repop = Now.AddMinutes(30);
        await using (var seed = NewDb(root, name))
        {
            foreach (var ls in new[] { 1, 2 })
            {
                seed.Linkshells.Add(new Linkshell { Id = ls, LinkshellName = $"LS{ls}" });
                seed.Tods.Add(new Tod
                {
                    Id = 10 + ls, LinkshellId = ls, MonsterName = "Fafnir", DayNumber = 1,
                    Time = repop.AddHours(-22), RepopTime = repop,
                });
                seed.HnmRecurringBoards.Add(new HnmRecurringBoard
                {
                    Id = ls, LinkshellId = ls, MonsterName = "Fafnir", Enabled = true, LeadHours = 1,
                });
            }
            await seed.SaveChangesAsync();
        }

        await new HnmRecurringBoardBackgroundService(scopes, NullLogger<HnmRecurringBoardBackgroundService>.Instance)
            .ProcessDueBoardsAsync(Now, CancellationToken.None);

        await using var db = NewDb(root, name);
        // Linkshell 2's board went up for the pop and its ToD is done with ...
        var posted = Assert.Single(await db.Events.ToListAsync());
        Assert.Equal(2, posted.LinkshellId);
        Assert.Equal(12, posted.SourceTodId);
        Assert.Equal(12, (await db.HnmRecurringBoards.SingleAsync(b => b.Id == 2)).LastSourceTodId);
        // ... and linkshell 1's failed attempt left nothing behind for that save to carry along.
        Assert.Null((await db.HnmRecurringBoards.SingleAsync(b => b.Id == 1)).LastSourceTodId);
    }

    [Fact]
    public async Task AutoStart_AFailingEvent_DoesNotKeepTheOthersFromGoingLive()
    {
        var (root, name, scopes) = NewJobDatabase(entity => entity is Event { Id: 1 });
        await using (var seed = NewDb(root, name))
        {
            foreach (var id in new[] { 1, 2 })
            {
                seed.Events.Add(new Event
                {
                    Id = id, LinkshellId = id, EventName = $"Dynamis {id}", EventType = "Dynamis",
                    AutoStart = true, StartTime = DateTime.UtcNow.AddMinutes(-1),
                });
            }
            await seed.SaveChangesAsync();
        }

        await new EventAutoStartBackgroundService(scopes, NullLogger<EventAutoStartBackgroundService>.Instance)
            .StartDueEventsAsync(CancellationToken.None);

        await using var db = NewDb(root, name);
        Assert.Null((await db.Events.SingleAsync(e => e.Id == 1)).CommencementStartTime);
        Assert.NotNull((await db.Events.SingleAsync(e => e.Id == 2)).CommencementStartTime);
    }

    [Fact]
    public async Task WindowAdvance_AFailingCamp_DoesNotFreezeTheOthersOnTheirOldWindow()
    {
        var (root, name, scopes) = NewJobDatabase(entity => entity is Event { Id: 1 });
        // Live 25 minutes on a 10-minute grid: both camps are due to be on window 3.
        var anchor = DateTime.UtcNow.AddMinutes(-25);
        await using (var seed = NewDb(root, name))
        {
            foreach (var id in new[] { 1, 2 })
            {
                seed.Events.Add(new Event
                {
                    Id = id, LinkshellId = id, EventName = "Fafnir", EventType = "HNM",
                    AssignedMonsterName = "Fafnir", StartTime = anchor, CommencementStartTime = anchor,
                    WindowAnchorAt = anchor, HnmWindowNumber = 1, SpawnWindowCount = 7, SpawnWindowMinutes = 10,
                });
            }
            await seed.SaveChangesAsync();
        }

        await new HnmWindowAdvanceBackgroundService(scopes, NullLogger<HnmWindowAdvanceBackgroundService>.Instance)
            .AdvanceLiveCampsAsync(CancellationToken.None);

        await using var db = NewDb(root, name);
        Assert.Equal(1, (await db.Events.SingleAsync(e => e.Id == 1)).HnmWindowNumber);
        Assert.Equal(3, (await db.Events.SingleAsync(e => e.Id == 2)).HnmWindowNumber);
    }

    // The list of due rows is read once, but each row is read again on its own context before it is
    // touched: one that an officer started (or ended) in the meantime is left alone.
    [Fact]
    public async Task AutoStart_AnEventStartedSinceTheListWasRead_IsNotStartedAgain()
    {
        var root = new InMemoryDatabaseRoot();
        var name = Guid.NewGuid().ToString();
        var officerStart = DateTime.UtcNow.AddMinutes(-3);
        await using (var seed = NewDb(root, name))
        {
            seed.Events.Add(new Event
            {
                Id = 1, LinkshellId = 1, EventName = "Dynamis", EventType = "Dynamis",
                AutoStart = true, StartTime = DateTime.UtcNow.AddMinutes(-5),
            });
            await seed.SaveChangesAsync();
        }

        // The officer presses Start right after the job lists the event: the job's FIRST context
        // (the id list) is the only one opened before the officer's save lands.
        var scopes = new StartAfterListing(NewOptions(root, name), root, name, officerStart);
        await new EventAutoStartBackgroundService(scopes, NullLogger<EventAutoStartBackgroundService>.Instance)
            .StartDueEventsAsync(CancellationToken.None);

        await using var db = NewDb(root, name);
        Assert.Equal(officerStart, (await db.Events.SingleAsync()).CommencementStartTime);
    }

    private sealed class StartAfterListing(
        DbContextOptions<ApplicationDbContext> options, InMemoryDatabaseRoot root, string name, DateTime officerStart)
        : IServiceScopeFactory
    {
        private readonly FreshContextScopes _inner = new(options);
        private int _scopes;

        public IServiceScope CreateScope()
        {
            if (++_scopes == 2)
            {
                using var officer = NewDb(root, name);
                officer.Events.Single().CommencementStartTime = officerStart;
                officer.SaveChanges();
            }
            return _inner.CreateScope();
        }
    }
}
