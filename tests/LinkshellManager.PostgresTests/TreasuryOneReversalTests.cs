using System;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// A treasury entry is reversed at most once. TreasuryJournalWriter checks before it writes, but two
// reversals at the same moment both pass that check; IX_JournalEntries_OneReversalPerEntry is what
// stops the second. The in-memory provider ignores unique indexes, so only these tests prove it.
[Collection("postgres")]
public class TreasuryOneReversalTests : IAsyncLifetime
{
    private const string IndexName = "IX_JournalEntries_OneReversalPerEntry";
    private static readonly TreasuryActor Officer = new("user-1", "Millhouse");

    private readonly PostgresFixture _pg;

    public TreasuryOneReversalTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static TreasuryJournalWriter NewWriter(ApplicationDbContext db) =>
        new(db, new LedgerAccountProvisioner(db));

    private static TreasuryEntryRequest Sale(long amount) =>
        new(TreasuryTransactionKinds.SoldAnItem, amount, new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc));

    // A linkshell with one confirmed 1,000 gil sale. Returns (linkshell id, sale id).
    private async Task<(int LinkshellId, int SaleId)> SeedSaleAsync()
    {
        await using var db = _pg.CreateContext();
        var ls = new Linkshell { LinkshellName = "LS" };
        db.Linkshells.Add(ls);
        await db.SaveChangesAsync();
        await new LedgerAccountProvisioner(db).EnsureAccountsAsync(ls.Id, CancellationToken.None);
        var sale = await NewWriter(db).RecordAsync(ls.Id, Sale(1_000), Officer, CancellationToken.None);
        await db.SaveChangesAsync();
        return (ls.Id, sale.Id);
    }

    private static Task<JournalEntry> LoadAsync(ApplicationDbContext db, int id) =>
        db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Id == id);

    private static void AssertUniqueViolation(DbUpdateException ex)
    {
        var pg = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
    }

    // A second Reversal row for the same entry, written past the writer's check, is refused by the
    // index itself (its own entry number, so only the reversal rule can be what says no).
    [Fact]
    public async Task SecondReversalRowIsRejectedByTheDatabase()
    {
        var (lsId, saleId) = await SeedSaleAsync();
        await using (var db = _pg.CreateContext())
        {
            await NewWriter(db).ReverseAsync(await LoadAsync(db, saleId), "Sold in error.", Officer, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            db.JournalEntries.Add(new JournalEntry
            {
                LinkshellId = lsId,
                Sequence = 999,
                EntryNumber = "TX-999",
                Status = JournalEntryStatuses.Draft,
                Kind = JournalEntryKinds.Reversal,
                ReversesJournalEntryId = saleId,
                CorrectionReason = "Double click.",
            });

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            AssertUniqueViolation(ex);
            Assert.Equal(IndexName, ((PostgresException)ex.InnerException!).ConstraintName);
        }
    }

    // Two officers press Reverse at once: both pass the writer's check, one save lands, the other is
    // turned away. Re-run the way the endpoint does (clear, read again) and it is told "already
    // reversed". The gil comes off once.
    [Fact]
    public async Task TwoReversalsAtOnce_OneLands_TheReRunIsRefused()
    {
        var (lsId, saleId) = await SeedSaleAsync();
        await using var first = _pg.CreateContext();
        await using var second = _pg.CreateContext();

        // Both build their reversal before either saves.
        await NewWriter(first).ReverseAsync(await LoadAsync(first, saleId), "Sold in error.", Officer, CancellationToken.None);
        var secondWriter = NewWriter(second);
        await secondWriter.ReverseAsync(await LoadAsync(second, saleId), "Sold in error.", Officer, CancellationToken.None);

        await first.SaveChangesAsync();
        AssertUniqueViolation(await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync()));

        second.ChangeTracker.Clear();
        await Assert.ThrowsAsync<AlreadyReversedTreasuryEntryException>(async () =>
            await secondWriter.ReverseAsync(await LoadAsync(second, saleId), "Sold in error.", Officer, CancellationToken.None));

        await using var check = _pg.CreateContext();
        Assert.Equal(1, await check.JournalEntries.CountAsync(e => e.Kind == JournalEntryKinds.Reversal));
        Assert.Equal(0, await NewWriter(check).GetCashOnHandAsync(lsId, CancellationToken.None));
    }

    // A Fix writes a Reversal AND a Correction pointing at the same entry. Only Reversal rows count
    // for the index, so a Fix still saves.
    [Fact]
    public async Task FixStillSaves()
    {
        var (lsId, saleId) = await SeedSaleAsync();
        await using (var db = _pg.CreateContext())
        {
            await NewWriter(db).CorrectAsync(await LoadAsync(db, saleId), Sale(900), "Wrong price.", Officer, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using var check = _pg.CreateContext();
        Assert.Equal(2, await check.JournalEntries.CountAsync(e => e.ReversesJournalEntryId == saleId));
        Assert.Equal(900, await NewWriter(check).GetCashOnHandAsync(lsId, CancellationToken.None));
    }

    // Why the migration checks first: production may already hold an entry reversed twice, and a
    // plain CREATE UNIQUE INDEX would fail the startup migration on it. With a duplicate in place the
    // migration's SQL still runs (and skips the index); the owner's pre-check lists the pair; once it
    // is resolved, the same SQL creates the index.
    [Fact]
    public async Task MigrationSkipsTheIndexOverAnOldDuplicate_ThenCreatesItOnceResolved()
    {
        var (lsId, saleId) = await SeedSaleAsync();
        await using var db = _pg.CreateContext();
        try
        {
            await NewWriter(db).ReverseAsync(await LoadAsync(db, saleId), "Sold in error.", Officer, CancellationToken.None);
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync($"DROP INDEX \"{IndexName}\"");
            db.JournalEntries.Add(new JournalEntry
            {
                LinkshellId = lsId,
                Sequence = 999,
                EntryNumber = "TX-999",
                Status = JournalEntryStatuses.Draft,
                Kind = JournalEntryKinds.Reversal,
                ReversesJournalEntryId = saleId,
                CorrectionReason = "Old double click.",
            });
            await db.SaveChangesAsync();

            await db.Database.ExecuteSqlRawAsync(OneReversalPerTreasuryEntry.UpSql);
            Assert.False(await IndexExistsAsync());
            Assert.Equal(2, await CountPreCheckRowsAsync());

            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"JournalEntries\" WHERE \"EntryNumber\" = 'TX-999'");
            Assert.Equal(0, await CountPreCheckRowsAsync());
            await db.Database.ExecuteSqlRawAsync(OneReversalPerTreasuryEntry.UpSql);
            Assert.True(await IndexExistsAsync());
        }
        finally
        {
            // The database is shared by every Postgres test class: always leave the index on.
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"JournalEntries\" WHERE \"EntryNumber\" = 'TX-999'");
            await db.Database.ExecuteSqlRawAsync(OneReversalPerTreasuryEntry.CreateIndexSql);
        }
    }

    private async Task<bool> IndexExistsAsync()
    {
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_indexes WHERE indexname = @name", conn);
        cmd.Parameters.AddWithValue("name", IndexName);
        return (long)(await cmd.ExecuteScalarAsync())! == 1;
    }

    private async Task<long> CountPreCheckRowsAsync()
    {
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT count(*) FROM ({OneReversalPerTreasuryEntry.PreCheckSql}) dup", conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
