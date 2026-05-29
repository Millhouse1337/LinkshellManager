// DbInspector — read-only Postgres inspection helper (+ one destructive reset).
//
// Reads the connection string from the APP_CS env var, falling back to the main
// app's user-secrets so it can run without copying credentials around. Excluded
// from the main web-app build; run by hand during development.
//
// Usage:
//   dotnet run --project tools/DbInspector -- tables             # list public tables
//   dotnet run --project tools/DbInspector -- history            # list applied EF migrations
//   dotnet run --project tools/DbInspector -- exists:TableName   # check a table exists
//   dotnet run --project tools/DbInspector -- reset-public <db>  # DESTRUCTIVE: drop+recreate
//                                                                # the public schema; <db> must
//                                                                # match the connected DB name
using Microsoft.Extensions.Configuration;
using Npgsql;

var argsList = args.ToList();
if (argsList.Count == 0)
{
    Console.Error.WriteLine("Usage: DbInspector <tables|history|exists:TableName|reset-public>");
    return 1;
}

var connectionString = Environment.GetEnvironmentVariable("APP_CS");
if (string.IsNullOrWhiteSpace(connectionString))
{
    // Fall back to the main app's user-secrets so the tool can run without the
    // caller having to copy the connection string into APP_CS. Keeps the
    // credential out of shell history and out of the tool caller's context.
    const string appUserSecretsId = "aspnet-LinkshellManagerDiscordApp-097d4206-0f91-471a-b0bb-fb7302e26d5f";
    var config = new ConfigurationBuilder()
        .AddUserSecrets(appUserSecretsId)
        .AddEnvironmentVariables()
        .Build();
    connectionString = config.GetConnectionString("DefaultConnection");
}
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("APP_CS not set and ConnectionStrings:DefaultConnection not found in user-secrets.");
    return 1;
}

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

var mode = argsList[0];
if (string.Equals(mode, "reset-public", StringComparison.OrdinalIgnoreCase))
{
    // Destructive: require the connected database name as confirmation so an
    // accidental run can never drop the wrong (e.g. production) schema.
    if (argsList.Count < 2 || !string.Equals(argsList[1], connection.Database, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            $"Refusing to reset. To confirm, name the connected database:\n" +
            $"  dotnet run --project tools/DbInspector -- reset-public {connection.Database}");
        return 1;
    }

    await using var resetCommand = new NpgsqlCommand("drop schema if exists public cascade; create schema public;", connection);
    await resetCommand.ExecuteNonQueryAsync();
    Console.WriteLine("reset");
    return 0;
}

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
