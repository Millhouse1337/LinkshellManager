using System;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LinkshellManager.Tests;

// Two payouts of the same review card at the same moment. The loser's save hits the
// one-credit-per-account index on DkpLedgerEntries; Post now re-runs the payout instead of failing,
// and the re-run finds the winner's rows. The real race runs in the Postgres tests.
public class CampCreditRaceRetryTests
{
    private static ApplicationDbContext NewDb() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DbUpdateException Rejection(string sqlState, string table) =>
        new("save failed", new PostgresException(
            "rejected", "ERROR", "ERROR", sqlState, tableName: table));

    [Fact]
    public async Task LostRace_ClearsTheFailedSaveAndPaysAgain()
    {
        await using var db = NewDb();
        var attempts = 0;

        var paid = await WindowEventDkpLedgerService.RetryIfCampCreditRaceAsync(db, () =>
        {
            attempts++;
            if (attempts == 1)
            {
                // The failed payout's rows must not go out with the re-run.
                db.DkpLedgerEntries.Add(new DkpLedgerEntry { LinkshellId = 1, AppUserId = "u", EntryType = "SnapshotEarned" });
                throw Rejection(PostgresErrorCodes.UniqueViolation, "DkpLedgerEntries");
            }
            Assert.Empty(db.ChangeTracker.Entries());
            return Task.FromResult(0);
        });

        Assert.Equal(0, paid);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, "AppUserEventWindows")]   // not a payout race
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, "DkpLedgerEntries")]
    public async Task OtherSaveFailures_AreNotRetried(string sqlState, string table)
    {
        await using var db = NewDb();
        var attempts = 0;

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            WindowEventDkpLedgerService.RetryIfCampCreditRaceAsync<int>(db, () =>
            {
                attempts++;
                throw Rejection(sqlState, table);
            }));
        Assert.Equal(1, attempts);
    }

    // The index the retry relies on: unique, per card and account, payout rows only. Audit rows
    // carry the card's id too and must stay free to stack.
    [Fact]
    public void Model_HasTheOneCreditPerAccountIndex()
    {
        using var db = NewDb();
        var entity = db.Model.FindEntityType(typeof(DkpLedgerEntry))!;

        var index = entity.GetIndexes().Single(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[]
            {
                nameof(DkpLedgerEntry.SourceWindowEventId), nameof(DkpLedgerEntry.AppUserId),
            }));

        Assert.True(index.IsUnique);
        Assert.Equal(
            "\"EntryType\" = 'SnapshotEarned' AND \"AppUserId\" IS NOT NULL AND \"SourceWindowEventId\" IS NOT NULL",
            index.GetFilter());
    }
}
