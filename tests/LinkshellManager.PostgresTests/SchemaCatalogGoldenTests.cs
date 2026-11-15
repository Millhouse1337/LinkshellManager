using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// A fingerprint of the whole schema a fresh database gets from the full migration chain: every
// column (type, length, NULL rule, default, identity), every index (its full definition), every
// constraint (with NOT VALID marked) and every sequence. It is compared with a committed golden file,
// Snapshots/SchemaCatalog.txt.
//
// Why: the migration squash (Round 4, D11) replaces the whole chain with one Baseline migration. A
// fresh database built from Baseline must come out exactly like this one, and production's catalog
// (the same query, pre-check I3, run read-only by the owner) must match too before the squash. Any
// migration that changes the schema also changes this file, so a change nobody meant shows up here.
//
// The query is pre-check I3 word for word, so the owner's production output can be diffed against
// this file. NEVER commit production output: this file is built from a fresh test database only.
//
// Regenerate (only when a schema change is MEANT, and review the diff before committing):
//   PowerShell:  $env:UPDATE_SCHEMA_GOLDEN=1; dotnet test tests/LinkshellManager.PostgresTests --filter SchemaCatalogGoldenTests; Remove-Item Env:UPDATE_SCHEMA_GOLDEN
//
// Its OWN database inside the test container, so no other test's tables or indexes can leak in.
[Collection("postgres")]
public class SchemaCatalogGoldenTests
{
    // Pre-check I3 (round4_precheck.sql), unchanged.
    public const string FingerprintSql = """
        SELECT 'column' AS kind, c.table_name::text || '.' || c.column_name::text AS name,
               c.data_type::text || coalesce('(' || c.character_maximum_length || ')', '')
                 || CASE WHEN c.is_nullable = 'NO' THEN ' NOT NULL' ELSE '' END
                 || coalesce(' DEFAULT ' || c.column_default::text, '')
                 || CASE WHEN c.is_identity = 'YES' THEN ' IDENTITY ' || c.identity_generation::text ELSE '' END AS definition
        FROM information_schema.columns c WHERE c.table_schema = 'public'
        UNION ALL
        SELECT 'index', i.tablename::text || '.' || i.indexname::text, i.indexdef FROM pg_indexes i WHERE i.schemaname = 'public'
        UNION ALL
        SELECT 'constraint', k.conrelid::regclass::text || '.' || k.conname::text, pg_get_constraintdef(k.oid) || CASE WHEN k.convalidated THEN '' ELSE ' [NOT VALID]' END
        FROM pg_constraint k WHERE k.connamespace = 'public'::regnamespace
        UNION ALL
        SELECT 'sequence', s.relname::text, NULL FROM pg_class s WHERE s.relkind = 'S' AND s.relnamespace = 'public'::regnamespace
        ORDER BY 1, 2
        """;

    private const string GoldenFileName = "SchemaCatalog.txt";

    private readonly PostgresFixture _pg;

    public SchemaCatalogGoldenTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task FreshDatabaseFromAllMigrations_MatchesTheGoldenCatalog()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "schema_golden_");
        try
        {
            await SchemaTestDatabase.MigrateToLatestAsync(connectionString);
            var actual = await SchemaTestDatabase.FingerprintAsync(connectionString);
            AssertMatchesGolden(actual);
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    private static void AssertMatchesGolden(string actual)
    {
        var path = Path.Combine(FindSnapshotsFolder(), GoldenFileName);
        if (Environment.GetEnvironmentVariable("UPDATE_SCHEMA_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"No golden file at {path}. Run once with UPDATE_SCHEMA_GOLDEN=1 to write it.");
        var expected = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').ToList();
        var got = actual.Split('\n').ToList();
        if (expected.SequenceEqual(got, StringComparer.Ordinal))
        {
            return;
        }

        // Shown as a set difference, so one added index reads as one line, not as every line after it.
        var missing = expected.Except(got, StringComparer.Ordinal).Take(40).ToList();
        var extra = got.Except(expected, StringComparer.Ordinal).Take(40).ToList();
        var report = new StringBuilder();
        report.Append("The schema built from the migrations no longer matches Snapshots/")
            .Append(GoldenFileName)
            .Append(". If the change is meant, regenerate it with UPDATE_SCHEMA_GOLDEN=1 and review the diff.\n");
        foreach (var line in missing)
        {
            report.Append("- ").Append(line).Append('\n');
        }
        foreach (var line in extra)
        {
            report.Append("+ ").Append(line).Append('\n');
        }
        Assert.Fail(report.ToString());
    }

    private static string FindSnapshotsFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var project = Path.Combine(directory.FullName, "tests", "LinkshellManager.PostgresTests");
            if (Directory.Exists(project))
            {
                var snapshots = Path.Combine(project, "Snapshots");
                Directory.CreateDirectory(snapshots);
                return snapshots;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not find tests/LinkshellManager.PostgresTests above " + AppContext.BaseDirectory);
    }
}

// Helpers for a test that needs its own database in the shared container: create, migrate,
// fingerprint the schema (pre-check I3), drop.
public static class SchemaTestDatabase
{
    // One line per catalog row: kind, name and definition, tab-separated. Sorted here with an
    // ordinal comparison, not by the database's collation, so the file is the same on every machine.
    public static async Task<string> FingerprintAsync(string connectionString)
    {
        var lines = new List<string>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaCatalogGoldenTests.FingerprintSql;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var definition = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                lines.Add(reader.GetString(0) + "\t" + reader.GetString(1) + "\t" + definition);
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines) + "\n";
    }

    // A new empty database in the test container; returns its connection string (no pooling, so
    // DropAsync is never blocked by a connection left open).
    public static async Task<string> CreateAsync(PostgresFixture pg, string prefix)
    {
        var databaseName = prefix + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(pg.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");
        return new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        }.ConnectionString;
    }

    public static async Task DropAsync(PostgresFixture pg, string connectionString)
    {
        var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database;
        await ExecuteAsync(pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
    }

    public static ApplicationDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .UseApplicationServiceProvider(PostgresFixture.IdentityServices)
            .Options);

    public static async Task MigrateToLatestAsync(string connectionString)
    {
        await using var db = CreateContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync();
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
