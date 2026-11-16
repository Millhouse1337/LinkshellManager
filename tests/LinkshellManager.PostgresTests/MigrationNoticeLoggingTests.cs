using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round 5, ops item 6. A migration's RAISE WARNING used to be invisible: Npgsql logs Postgres
// notices at Debug and the app logs at Information. MigrationNotices sends them to the app log,
// and MigrationHealth warns about a schema left half-done (a NOT VALID check, an invalid index,
// a guarded index a migration skipped or left plain, or a guarded NOT NULL it skipped).
//
// Each test has its OWN database inside the test container, dropped at the end.
[Collection("postgres")]
public class MigrationNoticeLoggingTests
{
    private readonly PostgresFixture _pg;

    public MigrationNoticeLoggingTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task NoticeAndWarning_ReachTheLog_AtTheirOwnLevels()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "notice_");
        try
        {
            var log = new CapturingLogger();
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                var result = await MigrationNotices.RunWithNoticesLoggedAsync(db, log, async () =>
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "DO $$ BEGIN RAISE NOTICE 'n'; RAISE WARNING 'w'; END $$");
                    return 7;
                });
                Assert.Equal(7, result);

                var info = Assert.Single(log.Entries, e => e.Level == LogLevel.Information);
                Assert.Equal("Postgres NOTICE during migration: n", info.Message);
                var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
                Assert.Equal("Postgres WARNING during migration: w", warning.Message);
                Assert.Equal(2, log.Entries.Count);

                // Once the work is done the listener is gone: a later notice is not logged.
                await db.Database.ExecuteSqlRawAsync("DO $$ BEGIN RAISE WARNING 'later'; END $$");
                Assert.Equal(2, log.Entries.Count);
            }
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // What Program.cs runs at start-up, through the helper, when the database does not exist yet:
    // adoption must still see "fresh", and Migrate must still create and build it.
    [Fact]
    public async Task StartUpSequence_OnAMissingDatabase_StillBuildsIt()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
        {
            Database = "notice_missing_" + Guid.NewGuid().ToString("N")[..8],
            Pooling = false,
        }.ConnectionString;
        try
        {
            var log = new CapturingLogger();
            await using var db = SchemaTestDatabase.CreateContext(connectionString);
            var adoption = await MigrationNotices.RunWithNoticesLoggedAsync(db, log, async () =>
            {
                var outcome = await MigrationBaseline.AdoptAsync(db);
                db.Database.Migrate();
                return outcome;
            });

            Assert.Equal(MigrationBaseline.Outcome.FreshDatabase, adoption);
            SchemaTestDatabase.AssertBuiltFromBaseline(db);
            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);

            // A database built from Baseline has nothing to warn about today.
            Assert.Equal(0, await MigrationHealth.LogProblemsAsync(db, log, CancellationToken.None));
            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // Every guarded index the health check names exists and is unique in a database built from
    // Baseline, and every guarded column is NOT NULL, so a healthy production start-up logs no
    // warning.
    [Fact]
    public async Task Health_OnABaselineDatabase_LogsNothing()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "health_ok_");
        try
        {
            await SchemaTestDatabase.MigrateToLatestAsync(connectionString);
            var log = new CapturingLogger();
            await using var db = SchemaTestDatabase.CreateContext(connectionString);

            Assert.Equal(0, await MigrationHealth.LogProblemsAsync(db, log, CancellationToken.None));
            Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
            Assert.Contains("IX_JournalEntries_OneReversalPerEntry", MigrationHealth.GuardedIndexes);
            Assert.Contains("IX_LinkshellMonsterTimings_LinkshellId_SpawnKey", MigrationHealth.GuardedIndexes);
            Assert.Contains("AppUserLinkshells.LinkshellRoleId", MigrationHealth.GuardedNotNullColumns);
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    [Fact]
    public async Task Health_MissingGuardedIndex_IsLogged()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "health_idx_");
        try
        {
            await SchemaTestDatabase.MigrateToLatestAsync(connectionString);
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                "DROP INDEX \"IX_JournalEntries_OneReversalPerEntry\"");
            var log = new CapturingLogger();
            await using var db = SchemaTestDatabase.CreateContext(connectionString);

            Assert.Equal(1, await MigrationHealth.LogProblemsAsync(db, log, CancellationToken.None));
            var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
            Assert.Contains("IX_JournalEntries_OneReversalPerEntry", warning.Message);
            Assert.Contains("missing", warning.Message);
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // A guarded migration that skips its unique index can leave the plain index of the same name
    // behind (CampSpawnKeySwitch does): present, but not doing its job.
    [Fact]
    public async Task Health_GuardedIndexThatIsNotUnique_IsLogged()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "health_plain_");
        try
        {
            await SchemaTestDatabase.MigrateToLatestAsync(connectionString);
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                "DROP INDEX \"IX_JournalEntries_OneReversalPerEntry\"; " +
                "CREATE INDEX \"IX_JournalEntries_OneReversalPerEntry\" ON \"JournalEntries\" (\"ReversesJournalEntryId\")");
            var log = new CapturingLogger();
            await using var db = SchemaTestDatabase.CreateContext(connectionString);

            Assert.Equal(1, await MigrationHealth.LogProblemsAsync(db, log, CancellationToken.None));
            var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
            Assert.Contains("IX_JournalEntries_OneReversalPerEntry", warning.Message);
            Assert.Contains("not unique", warning.Message);
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // MembershipRoleRequired skips NOT NULL with a WARNING when a member has no role; the health
    // check says so again on every start-up until it is fixed.
    [Fact]
    public async Task Health_GuardedColumnThatAllowsNull_IsLogged()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "health_null_");
        try
        {
            await SchemaTestDatabase.MigrateToLatestAsync(connectionString);
            Assert.Contains("AppUserLinkshells.LinkshellRoleId", MigrationHealth.GuardedNotNullColumns);
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                "ALTER TABLE \"AppUserLinkshells\" ALTER COLUMN \"LinkshellRoleId\" DROP NOT NULL");
            var log = new CapturingLogger();
            await using var db = SchemaTestDatabase.CreateContext(connectionString);

            Assert.Equal(1, await MigrationHealth.LogProblemsAsync(db, log, CancellationToken.None));
            var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
            Assert.Contains("AppUserLinkshells.LinkshellRoleId", warning.Message);
            Assert.Contains("allows NULL", warning.Message);
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    [Fact]
    public async Task Health_NotValidConstraint_AndInvalidIndex_AreLogged()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "health_bad_");
        try
        {
            await SchemaTestDatabase.MigrateToLatestAsync(connectionString);

            // A check added NOT VALID (old rows never checked).
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                "ALTER TABLE \"Linkshells\" ADD CONSTRAINT \"CK_Probe_NotValid\" CHECK (\"Id\" > 0) NOT VALID");

            // A unique index built CONCURRENTLY over duplicate rows fails and is left behind invalid.
            await SchemaTestDatabase.ExecuteAsync(connectionString,
                "CREATE TABLE health_probe (x int); INSERT INTO health_probe VALUES (1), (1);");
            await Assert.ThrowsAsync<PostgresException>(() => SchemaTestDatabase.ExecuteAsync(connectionString,
                "CREATE UNIQUE INDEX CONCURRENTLY \"IX_Probe_Invalid\" ON health_probe (x)"));

            var log = new CapturingLogger();
            await using var db = SchemaTestDatabase.CreateContext(connectionString);

            Assert.Equal(2, await MigrationHealth.LogProblemsAsync(db, log, CancellationToken.None));
            var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
            Assert.Equal(2, warnings.Count);
            Assert.Contains(warnings, m => m.Contains("CK_Probe_NotValid") && m.Contains("NOT VALID"));
            Assert.Contains(warnings, m => m.Contains("IX_Probe_Invalid") && m.Contains("invalid"));
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // Internal so other migration tests (MembershipRoleRequiredRoundTripTests) can check a notice's
    // level, not only its text.
    internal sealed record LogEntry(LogLevel Level, string Message);

    internal sealed class CapturingLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToList();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }
}
