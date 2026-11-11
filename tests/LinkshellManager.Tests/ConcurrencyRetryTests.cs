using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// P2.7: a member's balance (and an event row) is checked on save with Postgres's xmin, so the later
// of two overlapping DKP changes fails instead of erasing the earlier one. ConcurrencyRetry re-runs
// the losing action from scratch -- but only when that cannot repeat or drop anything. The real
// xmin check needs Postgres (LinkshellManager.PostgresTests/ConcurrencyTests.cs); here the conflict
// is thrown by hand to pin down the retry rules.
public class ConcurrencyRetryTests
{
    private static ApplicationDbContext NewDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DbUpdateConcurrencyException Conflict() => new("someone else saved this row");

    private static DkpLedgerWriter NewWriter(ApplicationDbContext db) =>
        new(db, new DkpPoolResolver(db, new DkpPoolProvisioner(db)), NullLogger<DkpLedgerWriter>.Instance);

    private static void SeedMember(ApplicationDbContext db)
    {
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "LS" });
        // Provisioned up front: creating the default pool mid-action is a save of its own.
        db.DkpPools.Add(DkpPoolDefaults.BuildDefaultPool(1));
        db.Users.Add(new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = 1, AppUserId = "bob", CharacterName = "Bob", LinkshellDkp = 100,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static Task<DkpLedgerEntry?> CreditAsync(ApplicationDbContext db, DkpLedgerWriter writer, AppUserLinkshell bob) =>
        writer.AppendAsync(
            bob, "AuditMisc", 5, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            DkpPoolRef.Derived(null), new DkpEntryContext(), CancellationToken.None);

    [Fact]
    public async Task LostRace_RunsAgainFromAnEmptyTracker()
    {
        await using var db = NewDb();
        var attempts = 0;

        var result = await ConcurrencyRetry.RunAsync(db, () =>
        {
            attempts++;
            if (attempts == 1)
            {
                // What the failed attempt staged must not go out with the re-run.
                db.Events.Add(new Event { Id = 1, LinkshellId = 1, EventName = "Faf" });
                throw Conflict();
            }
            Assert.Empty(db.ChangeTracker.Entries());
            return Task.FromResult("second try");
        });

        Assert.Equal("second try", result);
        Assert.Equal(2, attempts);
    }

    // The ledger writer remembers the next Sequence per member for the request. The failed attempt
    // took Sequence 1 but never saved it; the re-run must start from the database again, not at 2.
    [Fact]
    public async Task LostRace_ResetsTheLedgerWritersMemo()
    {
        await using var db = NewDb();
        SeedMember(db);
        var writer = NewWriter(db);
        var attempts = 0;

        var row = await ConcurrencyRetry.RunAsync(db, async () =>
        {
            attempts++;
            var bob = await db.AppUserLinkshells.SingleAsync();
            var entry = await CreditAsync(db, writer, bob);
            if (attempts == 1)
            {
                throw Conflict();
            }
            await db.SaveChangesAsync();
            return entry!;
        });

        Assert.Equal(2, attempts);
        Assert.Equal(1, row.Sequence);
        db.ChangeTracker.Clear();
        Assert.Equal(105, (await db.AppUserLinkshells.SingleAsync()).LinkshellDkp);
        Assert.Single(await db.DkpLedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task NoRace_RunsOnce()
    {
        await using var db = NewDb();
        var attempts = 0;

        var result = await ConcurrencyRetry.RunAsync(db, () =>
        {
            attempts++;
            return Task.FromResult("done");
        });

        Assert.Equal("done", result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task LosingEveryRetry_IsRethrown()
    {
        await using var db = NewDb();
        var attempts = 0;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            ConcurrencyRetry.RunAsync<string>(db, () =>
            {
                attempts++;
                throw Conflict();
            }));
        Assert.Equal(1 + ConcurrencyRetry.MaxRetries, attempts);
        Assert.Equal(0, db.ConcurrencyRetryDepth);
    }

    // Work staged before the call is the caller's. Clearing the tracker would silently throw it
    // away, so the action runs once and the conflict goes back to the caller.
    [Fact]
    public async Task WorkStagedBeforeTheCall_IsNeverCleared_RunsOnce()
    {
        await using var db = NewDb();
        db.Events.Add(new Event { Id = 1, LinkshellId = 1, EventName = "Faf" });
        var attempts = 0;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            ConcurrencyRetry.RunAsync<string>(db, () =>
            {
                attempts++;
                throw Conflict();
            }));

        Assert.Equal(1, attempts);
        Assert.Single(db.ChangeTracker.Entries<Event>());
    }

    // A save that already went through in this attempt must not be repeated by a re-run.
    [Fact]
    public async Task ASaveThatAlreadyCommitted_IsNotRepeated()
    {
        await using var db = NewDb();
        var attempts = 0;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            ConcurrencyRetry.RunAsync<string>(db, async () =>
            {
                attempts++;
                db.Events.Add(new Event { LinkshellId = 1, EventName = "Faf" });
                await db.SaveChangesAsync();
                throw Conflict();
            }));

        Assert.Equal(1, attempts);
        Assert.Single(await db.Events.ToListAsync());
    }

    // Only the outermost retry re-runs: an inner one clearing the tracker would drop entities the
    // outer action is still holding. The conflict travels up and the outer action runs again.
    [Fact]
    public async Task NestedRetry_OnlyTheOuterOneReruns()
    {
        await using var db = NewDb();
        var outerAttempts = 0;
        var innerAttempts = 0;

        var result = await ConcurrencyRetry.RunAsync(db, async () =>
        {
            outerAttempts++;
            return await ConcurrencyRetry.RunAsync(db, () =>
            {
                innerAttempts++;
                if (innerAttempts == 1)
                {
                    throw Conflict();
                }
                return Task.FromResult("ok");
            });
        });

        Assert.Equal("ok", result);
        Assert.Equal(2, outerAttempts);
        Assert.Equal(2, innerAttempts);
    }

    [Fact]
    public async Task FreshTick_RunsAgainOnALostRace_ThenGivesUp()
    {
        var attempts = 0;
        await ConcurrencyRetry.RunFreshTickAsync(() =>
        {
            attempts++;
            return attempts == 1 ? Task.FromException(Conflict()) : Task.CompletedTask;
        });
        Assert.Equal(2, attempts);

        attempts = 0;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => ConcurrencyRetry.RunFreshTickAsync(() =>
        {
            attempts++;
            return Task.FromException(Conflict());
        }));
        Assert.Equal(1 + ConcurrencyRetry.MaxRetries, attempts);
    }

    // The token is Postgres's xmin; it must stay out of Data Admin's forms (nobody edits it by hand)
    // while ConcurrencyStamp, a plain string Identity writes itself, stays visible.
    [Fact]
    public void RowVersions_AreMapped_AndHiddenFromDataAdmin()
    {
        using var db = NewDb();
        foreach (var type in new[] { typeof(Event), typeof(AppUserLinkshell) })
        {
            var version = db.Model.FindEntityType(type)!.FindProperty("Version")!;
            Assert.True(version.IsConcurrencyToken);
            Assert.True(DataAdminModel.IsRowVersion(version));
        }
        var stamp = db.Model.FindEntityType(typeof(AppUser))!.FindProperty(nameof(AppUser.ConcurrencyStamp))!;
        Assert.False(DataAdminModel.IsRowVersion(stamp));

        var catalog = new DataAdminCatalog(db.Model, DataAdminPolicy.Default);
        var events = catalog.Models.Single(model => model.ClrType == typeof(Event));
        Assert.DoesNotContain(events.AllColumns, column => column.Name == "Version");
    }

    // An action that could not re-run (or lost every re-run) saved nothing: the officer gets a 409
    // asking them to retry, not the generic "something went wrong" 500.
    [Fact]
    public async Task ApiExceptionHandler_AnswersALostRaceWith409()
    {
        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/activity/window-events/5/post";
        context.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(context, Conflict(), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(ConcurrencyRetry.ConflictMessage, body.RootElement.GetProperty("error").GetString());
    }
}
