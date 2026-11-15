// DbInspector — read-only Postgres inspection helper.
//
// The connection string comes ONLY from an environment variable you set on
// purpose for this run. It never falls back to the app's user-secrets: a
// default that silently picks up whatever database the app is configured for
// is how a dev tool ends up pointed at production.
//
//   APP_CS       a local / throwaway database.
//   LSM_PROD_CS  the production database. Read-only: the session is opened
//                with default_transaction_read_only=on.
//
// Set exactly one of them. Excluded from the main web-app build; run by hand.
//
// Usage:
//   dotnet run --project tools/DbInspector -- tables             # list public tables
//   dotnet run --project tools/DbInspector -- history            # list applied EF migrations
//   dotnet run --project tools/DbInspector -- exists:TableName   # check a table exists
//
// It used to have a destructive "reset-public" mode (drop and recreate the public schema). It was
// removed with the migration squash (Round 4, D11, ops-17): a wipe one routine command away was
// never worth it. To start a local database again, drop and create it with psql or Docker, then
// run "dotnet ef database update".
using Npgsql;

var argsList = args.ToList();
if (argsList.Count == 0)
{
    Console.Error.WriteLine("Usage: DbInspector <tables|history|exists:TableName>");
    return 1;
}

var localConnectionString = Environment.GetEnvironmentVariable("APP_CS");
var prodConnectionString = Environment.GetEnvironmentVariable("LSM_PROD_CS");
var hasLocal = !string.IsNullOrWhiteSpace(localConnectionString);
var hasProd = !string.IsNullOrWhiteSpace(prodConnectionString);

if (hasLocal && hasProd)
{
    // Refuse rather than guess which one you meant.
    Console.Error.WriteLine("Both APP_CS and LSM_PROD_CS are set. Set only one.");
    return 1;
}
if (!hasLocal && !hasProd)
{
    Console.Error.WriteLine(
        "No database chosen. Set APP_CS (local database) or LSM_PROD_CS (production, read-only) for this run.");
    return 1;
}

var mode = argsList[0];

string connectionString;
if (hasProd)
{
    // Make the server itself reject writes for this session, so a typo in a
    // future mode can't change production data.
    var builder = new NpgsqlConnectionStringBuilder(prodConnectionString);
    const string readOnlyOption = "-c default_transaction_read_only=on";
    builder.Options = string.IsNullOrWhiteSpace(builder.Options)
        ? readOnlyOption
        : $"{builder.Options} {readOnlyOption}";
    connectionString = builder.ConnectionString;
}
else
{
    connectionString = localConnectionString!;
}

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

var commandText = mode switch
{
    "tables" => "select table_name from information_schema.tables where table_schema = 'public' order by table_name;",
    "history" => "select \"MigrationId\" from \"__EFMigrationsHistory\" order by \"MigrationId\";",
    _ when mode.StartsWith("exists:", StringComparison.OrdinalIgnoreCase) =>
        "select count(*) from information_schema.tables where table_schema = 'public' and table_name = @name;",
    _ => throw new InvalidOperationException($"Unknown mode '{mode}'.")
};

await using var command = new NpgsqlCommand(commandText, connection);
if (mode.StartsWith("exists:", StringComparison.OrdinalIgnoreCase))
{
    command.Parameters.AddWithValue("name", mode["exists:".Length..]);
    var exists = Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    Console.WriteLine(exists ? "true" : "false");
    return 0;
}

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine(reader.GetString(0));
}

return 0;
