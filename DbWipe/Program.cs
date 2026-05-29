// DbWipe — DESTRUCTIVE local/dev database reset utility.
//
// TRUNCATEs every domain + ASP.NET Identity table in the target Postgres
// database (RESTART IDENTITY CASCADE), resetting the environment to empty.
// AspNetRoles / AspNetRoleClaims are preserved (roles are re-seeded at startup).
//
// Excluded from the main web-app build (see LinkshellManagerDiscordApp.csproj);
// run by hand against a local or test database only. NEVER point it at production.
//
// Usage (set LSM_CONN to the target Postgres connection string first):
//   dotnet run --project DbWipe -- verify           # read-only: print row counts
//   dotnet run --project DbWipe -- wipe <database>   # destructive; <database> must
//                                                    # match the connected DB name
using Npgsql;

var conn = Environment.GetEnvironmentVariable("LSM_CONN")
    ?? throw new InvalidOperationException("Set LSM_CONN env var with the Postgres connection string.");

await using var c = new NpgsqlConnection(conn);
await c.OpenAsync();
Console.WriteLine($"Connected to: {c.DataSource}/{c.Database}");

if (args.Length > 0 && args[0] == "verify")
{
    foreach (var t in new[] { "Linkshells", "Events", "WindowEvents", "Tods", "AttendanceSnapshots", "AppUserLinkshells", "AspNetUsers" })
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{t}\"";
        var n = await cmd.ExecuteScalarAsync();
        Console.WriteLine($"{t}: {n}");
    }
    return 0;
}

// The destructive path requires an explicit confirmation that names the
// connected database, so an accidental run (no args, or the wrong database)
// can never wipe data.
if (args.Length < 2 || args[0] != "wipe" || !string.Equals(args[1], c.Database, StringComparison.Ordinal))
{
    Console.Error.WriteLine(
        "Refusing to wipe. To confirm, name the connected database:\n" +
        $"  dotnet run --project DbWipe -- wipe {c.Database}\n" +
        "Use 'verify' to print row counts without changing anything.");
    return 1;
}

// Full reset: domain tables + Identity. CASCADE on AspNetUsers also drops
// AspNetUserRoles / AspNetUserLogins / AspNetUserTokens / AspNetUserClaims
// since they FK back to it. AspNetRoles + AspNetRoleClaims are preserved
// (role definitions are seeded at startup; user-role grants get reset).
var tables = new[]
{
    "AspNetUsers",
    "DiscordActivityUsers",
    "Linkshells",
    "AppUserLinkshells",
    "Invites",
    "Auctions", "AuctionItems", "Bids", "AuctionHistories",
    "Events", "Jobs", "AppUserEvents", "AppUserEventStatusLedgers", "DkpLedgerEntries",
    "EventHistories", "AppUserEventHistories",
    "EventLootDetails",
    "Tods", "TodLootDetails",
    "Notifications",
    "Rules",
    "Announcements",
    "Items",
    "RevenueEntries",
    "LinkshellRoles",
    "AddonApiTokens", "AddonPairingCodes",
    "EventAttendanceWindows", "AppUserEventWindows",
    "WindowEvents",
    "AttendanceSnapshots", "AttendanceSnapshotEntries",
    "PendingTodSubmissions", "PendingTodLootSubmissions",
    "PendingAttendanceWindowSubmissions", "PendingAttendanceWindowMemberSubmissions",
    "PendingAttendanceSnapshotSubmissions", "PendingAttendanceSnapshotEntries",
};

var quoted = string.Join(", ", tables.Select(t => $"\"{t}\""));
var sql = $"TRUNCATE TABLE {quoted} RESTART IDENTITY CASCADE;";

await using var truncCmd = c.CreateCommand();
truncCmd.CommandText = sql;
await truncCmd.ExecuteNonQueryAsync();
Console.WriteLine($"TRUNCATE complete ({tables.Length} tables).");
return 0;
