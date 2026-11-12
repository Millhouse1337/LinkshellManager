using System;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P3.11: an attendance window belongs to exactly one of Event / EventHistory, and an auction item to
// exactly one of Auction / AuctionHistory. A row with neither is invisible on every page; a row with
// both would be counted twice. The in-memory provider ignores CHECK constraints, so only these
// tests prove the rule is really in the database.
[Collection("postgres")]
public class OneParentConstraintTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public OneParentConstraintTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Parents(int LinkshellId, int EventId, int EventHistoryId, int AuctionId, int AuctionHistoryId);

    private async Task<Parents> SeedParentsAsync()
    {
        await using var db = _pg.CreateContext();
        var ls = new Linkshell { LinkshellName = "LS" };
        db.Linkshells.Add(ls);
        await db.SaveChangesAsync();

        var ev = new Event { LinkshellId = ls.Id, EventName = "Fafnir", EventType = "HNM" };
        var history = new EventHistory { LinkshellId = ls.Id, EventName = "Fafnir", EventType = "HNM" };
        var auction = new Auction { LinkshellId = ls.Id, AuctionTitle = "Live auction" };
        var auctionHistory = new AuctionHistory { LinkshellId = ls.Id, AuctionTitle = "Closed auction" };
        db.Events.Add(ev);
        db.EventHistories.Add(history);
        db.Auctions.Add(auction);
        db.AuctionHistories.Add(auctionHistory);
        await db.SaveChangesAsync();
        return new Parents(ls.Id, ev.Id, history.Id, auction.Id, auctionHistory.Id);
    }

    private static void AssertCheckViolation(DbUpdateException ex, string constraint)
    {
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.CheckViolation, pg.SqlState);
        Assert.Equal(constraint, pg.ConstraintName);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task WindowWithNeitherOrBothParentsIsRejected(bool live, bool archived)
    {
        var p = await SeedParentsAsync();
        await using var db = _pg.CreateContext();
        db.EventAttendanceWindows.Add(new EventAttendanceWindow
        {
            EventId = live ? p.EventId : null,
            EventHistoryId = archived ? p.EventHistoryId : null,
            SequenceNumber = 1,
            PostedAt = DateTime.UtcNow,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertCheckViolation(ex, "CK_EventAttendanceWindows_OneParent");
    }

    // The close's re-parent: one save sets the archive and clears the live camp. That is the one
    // write that touches both columns, so it must still pass.
    [Fact]
    public async Task WindowCanBeLiveThenMovedToTheArchiveInOneSave()
    {
        var p = await SeedParentsAsync();
        int windowId;
        await using (var db = _pg.CreateContext())
        {
            var window = new EventAttendanceWindow { EventId = p.EventId, SequenceNumber = 1, PostedAt = DateTime.UtcNow };
            db.EventAttendanceWindows.Add(window);
            await db.SaveChangesAsync();
            windowId = window.Id;
        }

        await using (var db = _pg.CreateContext())
        {
            var window = await db.EventAttendanceWindows.SingleAsync(w => w.Id == windowId);
            window.EventHistoryId = p.EventHistoryId;
            window.EventId = null;
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            var row = await db.EventAttendanceWindows.AsNoTracking().SingleAsync(w => w.Id == windowId);
            Assert.Null(row.EventId);
            Assert.Equal(p.EventHistoryId, row.EventHistoryId);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task AuctionItemWithNeitherOrBothParentsIsRejected(bool live, bool archived)
    {
        var p = await SeedParentsAsync();
        await using var db = _pg.CreateContext();
        db.AuctionItems.Add(new AuctionItem
        {
            AuctionId = live ? p.AuctionId : null,
            AuctionHistoryId = archived ? p.AuctionHistoryId : null,
            ItemName = "Ridill",
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        AssertCheckViolation(ex, "CK_AuctionItems_OneParent");
    }

    [Fact]
    public async Task AuctionItemWithOneParentIsAccepted()
    {
        var p = await SeedParentsAsync();
        await using var db = _pg.CreateContext();
        db.AuctionItems.AddRange(
            new AuctionItem { AuctionId = p.AuctionId, ItemName = "Ridill" },
            new AuctionItem { AuctionHistoryId = p.AuctionHistoryId, ItemName = "Kraken Club" });
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.AuctionItems.CountAsync());
    }

    // Why the migration says NOT VALID: production may hold an old bad row, and a plain ADD
    // CONSTRAINT would fail the startup migration on it. This puts a parentless window in the table,
    // re-runs the migration's SQL, and checks it still applies; then walks the owner's follow-up
    // (pre-check finds the row, VALIDATE refuses until it is fixed, then succeeds).
    [Fact]
    public async Task MigrationAppliesOverAnOldBadRow_AndOwnerCheckThenValidates()
    {
        var p = await SeedParentsAsync();
        await using var db = _pg.CreateContext();
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"EventAttendanceWindows\" DROP CONSTRAINT \"CK_EventAttendanceWindows_OneParent\"");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"EventAttendanceWindows\" (\"SequenceNumber\", \"PostedAt\", \"IsClosingWindow\", \"IsKillWindow\") " +
                "VALUES (1, now(), false, false)");

            // The migration's own statement goes through despite the bad row.
            await db.Database.ExecuteSqlRawAsync(AddOneParentCheckConstraints.AddWindowConstraintSql);

            // New bad writes are refused straight away, even though the old row was not checked.
            db.EventAttendanceWindows.Add(new EventAttendanceWindow { SequenceNumber = 2, PostedAt = DateTime.UtcNow });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            AssertCheckViolation(ex, "CK_EventAttendanceWindows_OneParent");
            db.ChangeTracker.Clear();

            // The owner's pre-check finds exactly the old row, and VALIDATE refuses while it is there.
            Assert.Equal(1, await CountPreCheckRowsAsync());
            var refused = await Assert.ThrowsAsync<PostgresException>(
                () => db.Database.ExecuteSqlRawAsync(AddOneParentCheckConstraints.ValidateSql));
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);

            // Fixed (here: given its archive), the pre-check comes back empty and VALIDATE succeeds.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"EventAttendanceWindows\" SET \"EventHistoryId\" = {p.EventHistoryId} WHERE \"EventId\" IS NULL AND \"EventHistoryId\" IS NULL");
            Assert.Equal(0, await CountPreCheckRowsAsync());
            await db.Database.ExecuteSqlRawAsync(AddOneParentCheckConstraints.ValidateSql);
        }
        finally
        {
            // The database is shared by every Postgres test class: always leave the constraint on.
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"EventAttendanceWindows\" WHERE num_nonnulls(\"EventId\", \"EventHistoryId\") <> 1");
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"EventAttendanceWindows\" DROP CONSTRAINT IF EXISTS \"CK_EventAttendanceWindows_OneParent\"");
            await db.Database.ExecuteSqlRawAsync(AddOneParentCheckConstraints.AddWindowConstraintSql);
        }
    }

    private async Task<long> CountPreCheckRowsAsync()
    {
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM ({AddOneParentCheckConstraints.PreCheckSql}) bad", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
