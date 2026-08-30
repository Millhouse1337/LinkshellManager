// DbWipe — DESTRUCTIVE local/dev database utilities.
//
// Two modes:
//
//   wipe  — TRUNCATEs every domain + ASP.NET Identity table (RESTART IDENTITY
//           CASCADE), resetting the whole database to empty.
//           AspNetRoles / AspNetRoleClaims are preserved.
//
//   scrub — Single-linkshell activity reset. KEEPS the linkshell row, its
//           members (AppUserLinkshells), roles (LinkshellRoles), and the
//           Announcements/Rules content. DELETES all of that linkshell's
//           events, loot, auctions/AH, ToDs, window events, attendance
//           snapshots, claim shields, party setups, pending submissions, DKP
//           ledger, inventory items, revenue, and addon tokens. Also zeroes
//           each member's LinkshellDkp + cached JobLevels and disconnects the
//           Google Sheet integration (clears spreadsheet id / OAuth token /
//           email / connected-at and sets SheetSyncEnabled = false).
//
// Excluded from the main web-app build (see LinkshellManagerDiscordApp.csproj);
// run by hand against a local or test database only. NEVER point it at production.
//
//   treasury / scrub-treasury
//         — The GIL only, for one linkshell. `treasury` is read-only; `scrub-treasury`
//           deletes that linkshell's transactions, any lock, and the dormant legacy
//           revenue rows, and un-marks any items flagged sold (their sale record is
//           going away with everything else). KEEPS the categories, so renames survive.
//           Events, loot, DKP, auctions, ToDs and the roster are untouched — that is
//           what makes it different from `scrub`.
//
// Usage (set LSM_CONN to the target Postgres connection string first):
//   dotnet run --project DbWipe -- verify                       # read-only: print row counts
//   dotnet run --project DbWipe -- treasury <Linkshell>         # read-only: one linkshell's gil
//   dotnet run --project DbWipe -- wipe <database>              # destructive; <database> must
//                                                               # match the connected DB name
//   dotnet run --project DbWipe -- scrub <database> <Linkshell> # destructive; both <database>
//                                                               # and the linkshell name must match
//   dotnet run --project DbWipe -- scrub-treasury <database> <Linkshell>   # destructive, gil only
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

if (args.Length > 0 && args[0] == "scrub")
{
    return await ScrubLinkshellAsync(c, args);
}

// treasury — READ-ONLY. What one linkshell's gil looks like right now: how many transactions are
// on the books and what they add up to. Run this before and after a treasury scrub so the change
// is something you saw rather than something you hope happened.
if (args.Length > 0 && args[0] == "treasury")
{
    return await ShowTreasuryAsync(c, args);
}

// scrub-treasury — DESTRUCTIVE, and deliberately the narrowest destructive mode here. Clears ONE
// linkshell's gil transactions and nothing else: events, loot, DKP, auctions, ToDs and the roster
// are all left alone, which is what separates it from `scrub`.
if (args.Length > 0 && args[0] == "scrub-treasury")
{
    return await ScrubTreasuryAsync(c, args);
}

// reset-schema: full schema reset for hosts where `dotnet ef database drop`
// can't run (e.g. DigitalOcean managed Postgres has no `postgres` maintenance
// DB and won't let you drop the connected database). Drops + recreates the
// `public` schema, removing every table INCLUDING __EFMigrationsHistory, so a
// squashed/consolidated migration can be applied fresh via `dotnet ef database
// update`. Requires naming the connected database to confirm.
if (args.Length > 0 && args[0] == "reset-schema")
{
    if (args.Length < 2 || !string.Equals(args[1], c.Database, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Refusing to reset. To confirm, name the connected database:\n" +
            $"  dotnet run --project DbWipe -- reset-schema {c.Database}");
        return 1;
    }

    await using var resetCmd = c.CreateCommand();
    resetCmd.CommandText = "DROP SCHEMA public CASCADE; CREATE SCHEMA public;";
    await resetCmd.ExecuteNonQueryAsync();
    Console.WriteLine(
        $"Schema reset complete on '{c.Database}'. All tables + migration history dropped.\n" +
        "Next: dotnet ef database update");
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
        "Use 'verify' to print row counts without changing anything.\n" +
        "Use 'scrub <database> <LinkshellName>' to reset a single linkshell's activity.");
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
    // Treasury: halves before entries (FK), and categories last since entries point at them.
    "JournalEntryLines", "JournalEntries", "LedgerAccounts", "LedgerPeriods",
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


// ---------------------------------------------------------------------------
// scrub: single-linkshell activity reset (keeps the linkshell + members/roles)
// ---------------------------------------------------------------------------
static async Task<int> ScrubLinkshellAsync(NpgsqlConnection c, string[] args)
{
    // Confirmation guard mirrors 'wipe': the connected DB name must be named
    // explicitly, plus the linkshell name to scrub.
    if (args.Length < 3 || !string.Equals(args[1], c.Database, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Refusing to scrub. To confirm, name the connected database AND the linkshell:\n" +
            $"  dotnet run --project DbWipe -- scrub {c.Database} \"<LinkshellName>\"");
        return 1;
    }

    var linkshellName = args[2];

    // Resolve the linkshell by exact name. Abort on zero or multiple matches so
    // an ambiguous name can never scrub the wrong (or several) linkshells.
    var ids = new List<int>();
    await using (var find = c.CreateCommand())
    {
        find.CommandText = "SELECT \"Id\" FROM \"Linkshells\" WHERE \"LinkshellName\" = @name";
        find.Parameters.AddWithValue("name", linkshellName);
        await using var r = await find.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetInt32(0));
    }

    if (ids.Count == 0)
    {
        Console.Error.WriteLine($"No linkshell named \"{linkshellName}\" found. Nothing scrubbed.");
        return 1;
    }
    if (ids.Count > 1)
    {
        Console.Error.WriteLine(
            $"Multiple linkshells named \"{linkshellName}\" (ids: {string.Join(", ", ids)}). " +
            "Refusing to scrub an ambiguous target.");
        return 1;
    }

    var lsId = ids[0];
    Console.WriteLine($"Scrubbing linkshell \"{linkshellName}\" (id {lsId}). Keeping members, roles, announcements, rules.");

    // Ordered, child-first deletes scoped to this linkshell. Each statement is
    // safe regardless of the FK's ON DELETE behavior because children are
    // removed before their parents. Tables without their own LinkshellId are
    // scoped via a subquery to their linkshell-scoped parent.
    //
    // (label, sql) pairs run inside a single transaction; any failure rolls the
    // whole thing back.
    var steps = new (string Label, string Sql)[]
    {
        // --- Auctions / AH ---
        ("Bids", "DELETE FROM \"Bids\" WHERE \"AuctionItemId\" IN (SELECT ai.\"Id\" FROM \"AuctionItems\" ai LEFT JOIN \"Auctions\" a ON ai.\"AuctionId\" = a.\"Id\" LEFT JOIN \"AuctionHistories\" ah ON ai.\"AuctionHistoryId\" = ah.\"Id\" WHERE a.\"LinkshellId\" = @ls OR ah.\"LinkshellId\" = @ls)"),
        ("AuctionItems", "DELETE FROM \"AuctionItems\" WHERE \"AuctionId\" IN (SELECT \"Id\" FROM \"Auctions\" WHERE \"LinkshellId\" = @ls) OR \"AuctionHistoryId\" IN (SELECT \"Id\" FROM \"AuctionHistories\" WHERE \"LinkshellId\" = @ls)"),
        ("Auctions", "DELETE FROM \"Auctions\" WHERE \"LinkshellId\" = @ls"),
        ("AuctionHistories", "DELETE FROM \"AuctionHistories\" WHERE \"LinkshellId\" = @ls"),

        // --- Events (live) + attendance windows + participation ---
        // Windows hang off a LIVE Event or, once the camp closes, off its EventHistory — the
        // archive re-parent moves them. Both sides have to be swept or a wipe leaves the closed
        // camps' rosters behind.
        ("AppUserEventWindows", "DELETE FROM \"AppUserEventWindows\" WHERE \"EventAttendanceWindowId\" IN (SELECT w.\"Id\" FROM \"EventAttendanceWindows\" w LEFT JOIN \"Events\" e ON w.\"EventId\" = e.\"Id\" LEFT JOIN \"EventHistories\" h ON w.\"EventHistoryId\" = h.\"Id\" WHERE e.\"LinkshellId\" = @ls OR h.\"LinkshellId\" = @ls)"),
        ("AppUserEventStatusLedgers", "DELETE FROM \"AppUserEventStatusLedgers\" WHERE \"EventId\" IN (SELECT \"Id\" FROM \"Events\" WHERE \"LinkshellId\" = @ls)"),
        ("EventAttendanceWindows", "DELETE FROM \"EventAttendanceWindows\" WHERE \"EventId\" IN (SELECT \"Id\" FROM \"Events\" WHERE \"LinkshellId\" = @ls) OR \"EventHistoryId\" IN (SELECT \"Id\" FROM \"EventHistories\" WHERE \"LinkshellId\" = @ls)"),
        ("AppUserEvents", "DELETE FROM \"AppUserEvents\" WHERE \"EventId\" IN (SELECT \"Id\" FROM \"Events\" WHERE \"LinkshellId\" = @ls)"),
        ("Jobs", "DELETE FROM \"Jobs\" WHERE \"EventId\" IN (SELECT \"Id\" FROM \"Events\" WHERE \"LinkshellId\" = @ls)"),

        // --- Loot details (link via Event OR EventHistory of this linkshell) ---
        ("EventLootDetails", "DELETE FROM \"EventLootDetails\" WHERE \"EventId\" IN (SELECT \"Id\" FROM \"Events\" WHERE \"LinkshellId\" = @ls) OR \"EventHistoryId\" IN (SELECT \"Id\" FROM \"EventHistories\" WHERE \"LinkshellId\" = @ls)"),
        ("AppUserEventHistories", "DELETE FROM \"AppUserEventHistories\" WHERE \"EventHistoryId\" IN (SELECT \"Id\" FROM \"EventHistories\" WHERE \"LinkshellId\" = @ls)"),

        // --- Window events ---
        ("WindowEventMemberDkps", "DELETE FROM \"WindowEventMemberDkps\" WHERE \"WindowEventId\" IN (SELECT \"Id\" FROM \"WindowEvents\" WHERE \"LinkshellId\" = @ls)"),

        // --- Attendance snapshots ---
        ("AttendanceSnapshotEntries", "DELETE FROM \"AttendanceSnapshotEntries\" WHERE \"SnapshotId\" IN (SELECT \"Id\" FROM \"AttendanceSnapshots\" WHERE \"LinkshellId\" = @ls)"),
        ("AttendanceSnapshots", "DELETE FROM \"AttendanceSnapshots\" WHERE \"LinkshellId\" = @ls"),

        // Snapshots reference Events/WindowEvents via SetNull, so they're gone
        // before we drop the parents below.
        ("WindowEvents", "DELETE FROM \"WindowEvents\" WHERE \"LinkshellId\" = @ls"),
        ("Events", "DELETE FROM \"Events\" WHERE \"LinkshellId\" = @ls"),
        ("EventHistories", "DELETE FROM \"EventHistories\" WHERE \"LinkshellId\" = @ls"),

        // --- ToDs (Events.SourceTodId already gone above) ---
        ("TodLootDetails", "DELETE FROM \"TodLootDetails\" WHERE \"TodId\" IN (SELECT \"Id\" FROM \"Tods\" WHERE \"LinkshellId\" = @ls)"),
        ("Tods", "DELETE FROM \"Tods\" WHERE \"LinkshellId\" = @ls"),

        // --- Claim shields ---
        ("ClaimShieldCaptureMembers", "DELETE FROM \"ClaimShieldCaptureMembers\" WHERE \"CaptureId\" IN (SELECT \"Id\" FROM \"ClaimShieldCaptures\" WHERE \"LinkshellId\" = @ls)"),
        ("ClaimShieldCaptures", "DELETE FROM \"ClaimShieldCaptures\" WHERE \"LinkshellId\" = @ls"),

        // --- Party setups (Alliance -> Party -> Slot) ---
        ("PartySetupSlots", "DELETE FROM \"PartySetupSlots\" WHERE \"PartySetupPartyId\" IN (SELECT p.\"Id\" FROM \"PartySetupParties\" p JOIN \"PartySetupAlliances\" a ON p.\"PartySetupAllianceId\" = a.\"Id\" JOIN \"PartySetups\" s ON a.\"PartySetupId\" = s.\"Id\" WHERE s.\"LinkshellId\" = @ls)"),
        ("PartySetupParties", "DELETE FROM \"PartySetupParties\" WHERE \"PartySetupAllianceId\" IN (SELECT a.\"Id\" FROM \"PartySetupAlliances\" a JOIN \"PartySetups\" s ON a.\"PartySetupId\" = s.\"Id\" WHERE s.\"LinkshellId\" = @ls)"),
        ("PartySetupAlliances", "DELETE FROM \"PartySetupAlliances\" WHERE \"PartySetupId\" IN (SELECT \"Id\" FROM \"PartySetups\" WHERE \"LinkshellId\" = @ls)"),
        ("PartySetups", "DELETE FROM \"PartySetups\" WHERE \"LinkshellId\" = @ls"),

        // --- Pending submissions ---
        ("PendingTodLootSubmissions", "DELETE FROM \"PendingTodLootSubmissions\" WHERE \"PendingTodSubmissionId\" IN (SELECT \"Id\" FROM \"PendingTodSubmissions\" WHERE \"LinkshellId\" = @ls)"),
        ("PendingTodSubmissions", "DELETE FROM \"PendingTodSubmissions\" WHERE \"LinkshellId\" = @ls"),
        ("PendingAttendanceWindowMemberSubmissions", "DELETE FROM \"PendingAttendanceWindowMemberSubmissions\" WHERE \"PendingAttendanceWindowSubmissionId\" IN (SELECT \"Id\" FROM \"PendingAttendanceWindowSubmissions\" WHERE \"LinkshellId\" = @ls)"),
        ("PendingAttendanceWindowSubmissions", "DELETE FROM \"PendingAttendanceWindowSubmissions\" WHERE \"LinkshellId\" = @ls"),
        ("PendingAttendanceSnapshotEntries", "DELETE FROM \"PendingAttendanceSnapshotEntries\" WHERE \"PendingAttendanceSnapshotSubmissionId\" IN (SELECT \"Id\" FROM \"PendingAttendanceSnapshotSubmissions\" WHERE \"LinkshellId\" = @ls)"),
        ("PendingAttendanceSnapshotSubmissions", "DELETE FROM \"PendingAttendanceSnapshotSubmissions\" WHERE \"LinkshellId\" = @ls"),

        // --- DKP ledger + economy ---
        ("DkpLedgerEntries", "DELETE FROM \"DkpLedgerEntries\" WHERE \"LinkshellId\" = @ls"),
        ("Items", "DELETE FROM \"Items\" WHERE \"LinkshellId\" = @ls"),
        ("RevenueEntries", "DELETE FROM \"RevenueEntries\" WHERE \"LinkshellId\" = @ls"),

        // --- Gil treasury ---
        // Order matters: halves first (they FK to both entries and categories), then entries, then the
        // categories themselves. Without these four the wipe would silently leave the treasury behind.
        ("JournalEntryLines", "DELETE FROM \"JournalEntryLines\" WHERE \"LinkshellId\" = @ls"),
        ("JournalEntries", "DELETE FROM \"JournalEntries\" WHERE \"LinkshellId\" = @ls"),
        ("LedgerAccounts", "DELETE FROM \"LedgerAccounts\" WHERE \"LinkshellId\" = @ls"),
        ("LedgerPeriods", "DELETE FROM \"LedgerPeriods\" WHERE \"LinkshellId\" = @ls"),

        // --- Addon tokens (user chose to revoke/delete) ---
        ("AddonPairingCodes", "DELETE FROM \"AddonPairingCodes\" WHERE \"LinkshellId\" = @ls"),
        ("AddonApiTokens", "DELETE FROM \"AddonApiTokens\" WHERE \"LinkshellId\" = @ls"),

        // --- Reset member DKP balances + cached job levels ---
        ("AppUserLinkshells (reset DKP/jobs)", "UPDATE \"AppUserLinkshells\" SET \"LinkshellDkp\" = 0, \"JobLevels\" = NULL WHERE \"LinkshellId\" = @ls"),

        // --- Disconnect Google Sheet integration ---
        ("Linkshells (disconnect Google Sheet)", "UPDATE \"Linkshells\" SET \"GoogleSpreadsheetId\" = NULL, \"GoogleSheetTabName\" = NULL, \"GoogleOAuthRefreshTokenEnc\" = NULL, \"GoogleOAuthUserEmail\" = NULL, \"GoogleOAuthConnectedAt\" = NULL, \"SheetSyncEnabled\" = false WHERE \"Id\" = @ls"),
    };

    await using var tx = await c.BeginTransactionAsync();
    try
    {
        var totalRows = 0;
        foreach (var (label, stepSql) in steps)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = stepSql;
            cmd.Parameters.AddWithValue("ls", lsId);
            var affected = await cmd.ExecuteNonQueryAsync();
            totalRows += affected;
            Console.WriteLine($"  {label}: {affected}");
        }

        await tx.CommitAsync();
        Console.WriteLine($"Scrub complete. {steps.Length} statements, {totalRows} rows affected. " +
                          $"Linkshell \"{linkshellName}\" kept (members/roles/announcements/rules intact).");
        return 0;
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        Console.Error.WriteLine($"Scrub FAILED and was rolled back: {ex.Message}");
        return 1;
    }
}

// Resolve a linkshell by EXACT name, refusing zero or multiple matches so an ambiguous name can
// never touch the wrong (or several) linkshells. Same contract as ScrubLinkshellAsync's inline
// lookup, which predates this and is left alone.
static async Task<int?> FindLinkshellIdAsync(NpgsqlConnection c, string linkshellName)
{
    var ids = new List<int>();
    await using (var find = c.CreateCommand())
    {
        find.CommandText = "SELECT \"Id\" FROM \"Linkshells\" WHERE \"LinkshellName\" = @name";
        find.Parameters.AddWithValue("name", linkshellName);
        await using var r = await find.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetInt32(0));
    }

    if (ids.Count == 0)
    {
        Console.Error.WriteLine($"No linkshell named \"{linkshellName}\" found.");
        await ListLinkshellsAsync(c);
        return null;
    }
    if (ids.Count > 1)
    {
        Console.Error.WriteLine(
            $"Multiple linkshells named \"{linkshellName}\" (ids: {string.Join(", ", ids)}). " +
            "Refusing an ambiguous target.");
        return null;
    }
    return ids[0];
}

// The names actually available, printed when a lookup misses. A typo'd or half-remembered name is
// the likeliest reason to be here, and guessing again blind is the slowest way out.
static async Task ListLinkshellsAsync(NpgsqlConnection c)
{
    Console.Error.WriteLine("Linkshells on this database:");
    await using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT \"Id\", \"LinkshellName\" FROM \"Linkshells\" ORDER BY \"Id\"";
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
    {
        Console.Error.WriteLine($"  {r.GetInt32(0)}  {r.GetString(1)}");
    }
}

// READ-ONLY. What is on the books for one linkshell.
//
// The balances are recomputed here from the halves rather than read from anywhere, because nothing
// stores them — the app derives them the same way on every request (TreasuryBalanceService), so
// this agrees with the website by construction.
static async Task<int> ShowTreasuryAsync(NpgsqlConnection c, string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: dotnet run --project DbWipe -- treasury <LinkshellName>");
        await ListLinkshellsAsync(c);
        return 1;
    }

    var lsId = await FindLinkshellIdAsync(c, args[1]);
    if (lsId is null) return 1;

    Console.WriteLine($"Treasury for \"{args[1]}\" (linkshell id {lsId}):");

    var counts = new (string Label, string Sql)[]
    {
        ("Transactions", "SELECT COUNT(*) FROM \"JournalEntries\" WHERE \"LinkshellId\" = @ls"),
        ("  of those, drafts", "SELECT COUNT(*) FROM \"JournalEntries\" WHERE \"LinkshellId\" = @ls AND \"Status\" <> 'Confirmed'"),
        ("Halves", "SELECT COUNT(*) FROM \"JournalEntryLines\" WHERE \"LinkshellId\" = @ls"),
        ("Categories", "SELECT COUNT(*) FROM \"LedgerAccounts\" WHERE \"LinkshellId\" = @ls"),
        ("Lock rows", "SELECT COUNT(*) FROM \"LedgerPeriods\" WHERE \"LinkshellId\" = @ls"),
        ("Legacy revenue rows", "SELECT COUNT(*) FROM \"RevenueEntries\" WHERE \"LinkshellId\" = @ls"),
        ("Items marked sold", "SELECT COUNT(*) FROM \"Items\" WHERE \"LinkshellId\" = @ls AND \"IsSold\" = true"),
    };

    foreach (var (label, sql) in counts)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("ls", lsId.Value);
        Console.WriteLine($"  {label,-22}: {await cmd.ExecuteScalarAsync()}");
    }

    // Confirmed halves only — a draft is a scratch pad and is not on the books.
    await using (var cmd = c.CreateCommand())
    {
        cmd.CommandText =
            "SELECT l.\"AccountNumber\" / 1000 AS band, SUM(l.\"Amount\") AS total " +
            "FROM \"JournalEntryLines\" l JOIN \"JournalEntries\" e ON l.\"JournalEntryId\" = e.\"Id\" " +
            "WHERE l.\"LinkshellId\" = @ls AND e.\"Status\" = 'Confirmed' " +
            "GROUP BY band ORDER BY band";
        cmd.Parameters.AddWithValue("ls", lsId.Value);
        Console.WriteLine("  Balances (confirmed):");
        await using var r = await cmd.ExecuteReaderAsync();
        var any = false;
        while (await r.ReadAsync())
        {
            any = true;
            // 1000s = what it has, 2000s = what it owes, 3000s = worth, 4000s = in, 5000s = out.
            var name = r.GetInt32(0) switch
            {
                1 => "gil on hand / owed to us",
                2 => "we owe",
                3 => "starting balance",
                4 => "gil in",
                5 => "gil out",
                _ => "other",
            };
            Console.WriteLine($"    {name,-26}: {r.GetInt64(1),15:N0}");
        }
        if (!any) Console.WriteLine("    (nothing on the books)");
    }

    return 0;
}

// DESTRUCTIVE. Clears ONE linkshell's gil and nothing else.
//
// Deleted: every transaction and both of its halves, any lock, and the dormant legacy revenue rows
// the one-time conversion already read. Items marked sold are un-marked, because their sale record
// is going away and an item flagged sold with nothing behind it is a lie the inventory page would
// keep telling.
//
// KEPT: the categories (LedgerAccounts), so any renames survive and the balance sheet still reads
// in the linkshell's own words. They are a settings list, not history. Everything outside the
// treasury — roster, events, loot, DKP, auctions, ToDs — is untouched.
//
// Requires naming BOTH the connected database and the linkshell, the same double confirmation
// `wipe` and `reset-schema` use.
static async Task<int> ScrubTreasuryAsync(NpgsqlConnection c, string[] args)
{
    if (args.Length < 3 || !string.Equals(args[1], c.Database, StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "Refusing to clear a treasury. To confirm, name the connected database AND the linkshell:\n" +
            $"  dotnet run --project DbWipe -- scrub-treasury {c.Database} <LinkshellName>\n" +
            "Use 'treasury <LinkshellName>' first to see what is there without changing anything.");
        return 1;
    }

    var linkshellName = args[2];
    var lsId = await FindLinkshellIdAsync(c, linkshellName);
    if (lsId is null) return 1;

    Console.WriteLine(
        $"Clearing the treasury for \"{linkshellName}\" (id {lsId}). " +
        "Categories kept; roster, events, loot, DKP and auctions untouched.");

    var steps = new (string Label, string Sql)[]
    {
        // Halves first: they FK to both the transaction and the category.
        ("JournalEntryLines", "DELETE FROM \"JournalEntryLines\" WHERE \"LinkshellId\" = @ls"),
        // Self-referencing (a reversal points at what it reversed), so break the link before the rows.
        ("JournalEntries (unlink reversals)", "UPDATE \"JournalEntries\" SET \"ReversesJournalEntryId\" = NULL WHERE \"LinkshellId\" = @ls"),
        ("JournalEntries", "DELETE FROM \"JournalEntries\" WHERE \"LinkshellId\" = @ls"),
        ("LedgerPeriods", "DELETE FROM \"LedgerPeriods\" WHERE \"LinkshellId\" = @ls"),
        ("RevenueEntries (legacy)", "DELETE FROM \"RevenueEntries\" WHERE \"LinkshellId\" = @ls"),
        ("Items (un-mark sold)", "UPDATE \"Items\" SET \"IsSold\" = false, \"SoldPrice\" = NULL, \"RevenueEntryId\" = NULL WHERE \"LinkshellId\" = @ls AND (\"IsSold\" = true OR \"RevenueEntryId\" IS NOT NULL)"),
    };

    await using var tx = await c.BeginTransactionAsync();
    try
    {
        var totalRows = 0;
        foreach (var (label, stepSql) in steps)
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = stepSql;
            cmd.Parameters.AddWithValue("ls", lsId.Value);
            var affected = await cmd.ExecuteNonQueryAsync();
            totalRows += affected;
            Console.WriteLine($"  {label}: {affected}");
        }

        await tx.CommitAsync();
        Console.WriteLine(
            $"Treasury cleared. {steps.Length} statements, {totalRows} rows affected. " +
            "Gil on hand is now 0 and the transactions list is empty.");
        return 0;
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        Console.Error.WriteLine($"Treasury clear FAILED and was rolled back: {ex.Message}");
        return 1;
    }
}
