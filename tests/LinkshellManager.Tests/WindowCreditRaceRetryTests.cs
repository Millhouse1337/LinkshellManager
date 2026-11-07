using System;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LinkshellManager.Tests;

// Two officers posting the same window at the same moment, both naming the same person. The loser's
// insert hits the unique index on AppUserEventWindows; its post now re-runs once instead of failing.
public class WindowCreditRaceRetryTests
{
    private static ApplicationDbContext NewDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DbUpdateException Rejection(string sqlState, string table) =>
        new("save failed", new PostgresException(
            "rejected", "ERROR", "ERROR", sqlState, tableName: table));

    [Fact]
    public async Task LostRace_ClearsTheFailedSaveAndRunsAgain()
    {
        await using var db = NewDb();
        var attempts = 0;

        var result = await AddonApiController.RetryOnceIfWindowCreditRaceAsync(db, () =>
        {
            attempts++;
            if (attempts == 1)
            {
                // What the failed save left behind must not go out with the retry.
                db.Events.Add(new Event { Id = 1, LinkshellId = 1, EventName = "faf", EventType = "HNM" });
                throw Rejection(PostgresErrorCodes.UniqueViolation, "AppUserEventWindows");
            }
            Assert.Empty(db.ChangeTracker.Entries());
            return Task.FromResult("second try");
        });

        Assert.Equal("second try", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task NoRace_RunsOnce()
    {
        await using var db = NewDb();
        var attempts = 0;

        var result = await AddonApiController.RetryOnceIfWindowCreditRaceAsync(db, () =>
        {
            attempts++;
            return Task.FromResult("done");
        });

        Assert.Equal("done", result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task LosingTwice_IsRethrown()
    {
        await using var db = NewDb();
        var attempts = 0;

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddonApiController.RetryOnceIfWindowCreditRaceAsync<string>(db, () =>
            {
                attempts++;
                throw Rejection(PostgresErrorCodes.UniqueViolation, "AppUserEventWindows");
            }));
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, "EventAttendanceWindows")]   // handled elsewhere
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, "AppUserEventWindows")]
    public async Task OtherSaveFailures_AreNotRetried(string sqlState, string table)
    {
        await using var db = NewDb();
        var attempts = 0;

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            AddonApiController.RetryOnceIfWindowCreditRaceAsync<string>(db, () =>
            {
                attempts++;
                throw Rejection(sqlState, table);
            }));
        Assert.Equal(1, attempts);
    }
}
