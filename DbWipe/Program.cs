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
    return;
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
