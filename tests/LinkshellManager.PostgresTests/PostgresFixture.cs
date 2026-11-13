using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Every test class that needs the real database joins this collection with [Collection("postgres")].
// xunit then creates ONE PostgresFixture for all of them (one container, migrations applied once)
// and runs those classes one after another, so ResetAsync in one test never wipes another's rows.
[CollectionDefinition("postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
}

// Starts a throwaway Postgres in Docker and builds the full schema from our migrations, the same
// way production gets it. The container is removed when the test run ends.
public sealed class PostgresFixture : IAsyncLifetime
{
    // Production runs Postgres 18, so test on that.
    private const string Image = "postgres:18";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    private NpgsqlDataSource? _dataSource;
    private string _truncateSql = string.Empty;

    public string ConnectionString => _container.GetConnectionString();

    // The one app setting the model needs: the Identity key length the app sets in Program.cs.
    // Without it the test model differs from the migrations, and EF Core 10's Migrate() refuses to
    // run (PendingModelChangesWarning) -- the same check production runs at startup, so keeping
    // it on here is what proves a deploy will boot.
    public static IServiceProvider IdentityServices { get; } = new ServiceCollection()
        .Configure<IdentityOptions>(o => o.Stores.MaxLengthForKeys = ApplicationDbContext.IdentityKeyMaxLength)
        .BuildServiceProvider();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Same data source settings as Program.cs: without EnableDynamicJson, writing the jsonb
        // int[] column (AppUserLinkshell.JobLevels) throws.
        var builder = new NpgsqlDataSourceBuilder(ConnectionString);
        builder.EnableDynamicJson();
        _dataSource = builder.Build();

        await using (var db = CreateContext())
        {
            await db.Database.MigrateAsync();
        }

        _truncateSql = await BuildTruncateSqlAsync();
    }

    public async Task DisposeAsync()
    {
        if (_dataSource != null)
        {
            await _dataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    // A fresh context on the shared database. Make a second one when a test needs two "requests"
    // (for example two officers pressing Post at once).
    //
    // interceptors: hooks into this context's saves, for a test that needs something to happen at an
    // exact moment (another request committing between this one's read and its save).
    public ApplicationDbContext CreateContext(params IInterceptor[] interceptors) =>
        CreateContextWithAuctionQueue(null, interceptors);

    // The same, but the context also feeds an auction card queue, for a test that checks when a
    // save asks for the Discord auction card to be edited.
    public ApplicationDbContext CreateContextWithAuctionQueue(
        DiscordAuctionChannelQueue? auctionChannelQueue, params IInterceptor[] interceptors)
    {
        if (_dataSource == null)
        {
            throw new InvalidOperationException("The Postgres container has not started yet.");
        }

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_dataSource)
            .UseApplicationServiceProvider(IdentityServices)
            .AddInterceptors(interceptors)
            .Options;
        return new ApplicationDbContext(options, auctionChannelQueue: auctionChannelQueue);
    }

    // Empties every app table (ids start again from 1) so each test begins from a blank database.
    // The migrations history is kept, so the schema itself stays in place. Call it at the START of
    // each test rather than the end, so a failed test never leaves rows behind for the next one.
    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(_truncateSql);
    }

    // Built once, after the migrations, so a table added by a future migration is wiped too.
    private async Task<string> BuildTruncateSqlAsync()
    {
        await using var connection = await _dataSource!.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_type = 'BASE TABLE' " +
            "AND table_name <> '__EFMigrationsHistory' ORDER BY table_name";

        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        // Quoted because our table names are mixed case ("AppUserEventWindows").
        var quoted = string.Join(", ", tables.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));
        return $"TRUNCATE TABLE {quoted} RESTART IDENTITY CASCADE;";
    }
}
