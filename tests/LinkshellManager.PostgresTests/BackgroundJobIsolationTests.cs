using System;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P3.9 (backend-08): auto-start used to start every due event on one context and save them in one
// go. An officer editing ONE of them between the job's read and its save tripped that event's xmin
// check (Event.Version), and the whole save was turned away -- nobody's event went live. With the
// edit landing on every retry, no event in any linkshell went live at all. Now each event is started
// and saved on its own, so the race costs only the event being edited, and only until the next tick.
[Collection("postgres")]
public class BackgroundJobIsolationTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public BackgroundJobIsolationTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // A fresh context per scope -- what production's DI hands each per-event pass.
    private sealed class FreshContextScopes(PostgresFixture pg, IInterceptor[] interceptors) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(pg.CreateContext(interceptors));

        private sealed class Scope(ApplicationDbContext db) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) =>
                serviceType == typeof(ApplicationDbContext) ? db : null;
            public void Dispose() => db.Dispose();
        }
    }

    // An officer who saves the contested event every time the job is about to: their UPDATE commits
    // first and moves the row's xmin, so the job's save of that event is always the stale one.
    private sealed class OfficerEditsFirst(PostgresFixture pg, int contestedEventId) : SaveChangesInterceptor
    {
        public int Edits { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var savingContested = false;
            foreach (var entry in eventData.Context!.ChangeTracker.Entries<Event>())
            {
                savingContested |= entry.State == EntityState.Modified && entry.Entity.Id == contestedEventId;
            }
            if (savingContested)
            {
                Edits++;
                await using var officer = pg.CreateContext();
                await officer.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"Events\" SET \"Details\" = {"officer edit " + Edits} WHERE \"Id\" = {contestedEventId}",
                    cancellationToken);
            }
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private async Task<(int First, int Second)> SeedTwoDueEventsAsync()
    {
        await using var db = _pg.CreateContext();
        var first = new Linkshell { LinkshellName = "LS one" };
        var second = new Linkshell { LinkshellName = "LS two" };
        db.Linkshells.AddRange(first, second);
        await db.SaveChangesAsync();

        var due = DateTime.UtcNow.AddMinutes(-1);
        var contested = new Event
        {
            LinkshellId = first.Id, EventName = "Dynamis", EventType = "Dynamis", AutoStart = true, StartTime = due,
        };
        var other = new Event
        {
            LinkshellId = second.Id, EventName = "Sky", EventType = "Sky", AutoStart = true, StartTime = due,
        };
        db.Events.Add(contested);
        await db.SaveChangesAsync();
        db.Events.Add(other);
        await db.SaveChangesAsync();
        return (contested.Id, other.Id);
    }

    private Task StartDueEventsAsync(params IInterceptor[] interceptors) =>
        new EventAutoStartBackgroundService(
                new FreshContextScopes(_pg, interceptors), NullLogger<EventAutoStartBackgroundService>.Instance)
            .StartDueEventsAsync(CancellationToken.None);

    [Fact]
    public async Task AnEventThatKeepsLosingARace_DoesNotKeepTheOthersFromGoingLive()
    {
        var (contestedId, otherId) = await SeedTwoDueEventsAsync();
        var officer = new OfficerEditsFirst(_pg, contestedId);

        await StartDueEventsAsync(officer);

        // The first try and every retry lost to the officer.
        Assert.Equal(ConcurrencyRetry.MaxRetries + 1, officer.Edits);
        await using (var db = _pg.CreateContext())
        {
            Assert.Null((await db.Events.SingleAsync(e => e.Id == contestedId)).CommencementStartTime);
            Assert.NotNull((await db.Events.SingleAsync(e => e.Id == otherId)).CommencementStartTime);
        }

        // The officer is done: the next tick starts it, on top of their edit rather than over it.
        await StartDueEventsAsync();

        await using (var db = _pg.CreateContext())
        {
            var contested = await db.Events.SingleAsync(e => e.Id == contestedId);
            Assert.NotNull(contested.CommencementStartTime);
            Assert.Equal("officer edit " + officer.Edits, contested.Details);
        }
    }
}
