<#
.SYNOPSIS
    DESTRUCTIVE cleanup: purge past events, the DKP ledger, job ratings, and
    event comments from the LinkshellManager Postgres database.

.DESCRIPTION
    Deletes, child-first inside a single transaction:
        1. JobRatings              (all peer job ratings)
        2. EventComments           (all post-event discussion comments)
        3. DkpLedgerEntries        (the entire DKP history ledger)
        4. AppUserEventHistories   (per-member attendance rows of past events)
        5. EventHistories          ("Past events" list)
    Optionally (-ResetBalances) also zeroes each member's DKP: the stored
    LinkshellDkp balance, the imported seed (SeededDkpEarned / SeededDkpSpent)
    and the two ledger watermarks (DkpSeedLedgerId, DkpPoolLedgerFromId). The DKP
    sheet adds the seed to the ledger, so leaving it would keep old DKP showing.

    Deleting EventHistories also deletes, through their cascading foreign keys,
    each past event's archived attendance windows (EventAttendanceWindows and
    their AppUserEventWindows) and its comments (EventComments). Loot, ledger, Claim Shield and window-event rows
    that point at a past event are kept and simply unlinked (SetNull).

    DEFAULTS TO A DRY RUN (prints counts, changes nothing). Pass -Execute to apply.

    There is NO default database. Pass -ConnectionString, or set the
    LSM_PURGE_CONNECTION environment variable. It used to fall back to the web
    app's user-secrets, but those can point at PRODUCTION, so one forgotten
    argument could wipe the live ledger. Check the "Target DB" line it prints
    before passing -Execute.

    Windows PowerShell 5.1 can't load the .NET Npgsql.dll, so this generates a
    tiny throwaway .NET 10 console tool and runs it via `dotnet run` (Npgsql 10.0.3,
    restores offline from the existing NuGet cache).

.EXAMPLE
    # See what WOULD be deleted, across all linkshells (safe, read-only):
    powershell -ExecutionPolicy Bypass -File tools\purge-history.ps1 -ConnectionString "Host=localhost;Port=55432;Database=lsm_test;Username=postgres;Password=..."

.EXAMPLE
    # Actually delete, scoped to one linkshell, and reset its members' DKP to 0:
    $env:LSM_PURGE_CONNECTION = "Host=localhost;Port=55432;Database=lsm_test;Username=postgres;Password=..."
    powershell -ExecutionPolicy Bypass -File tools\purge-history.ps1 -LinkshellName "AltanasFeet" -ResetBalances -Execute
#>
[CmdletBinding()]
param(
    # Postgres connection string. Required, or set LSM_PURGE_CONNECTION; there is
    # deliberately no default.
    [string]$ConnectionString,

    # Limit the purge to a single linkshell (by exact name). Omit = all linkshells.
    [string]$LinkshellName,

    # Also zero each member's DKP (in scope): LinkshellDkp, the imported seed and
    # the ledger watermarks, so balances match the now-empty ledger.
    [switch]$ResetBalances,

    # Required to actually delete. Without it the script only prints counts.
    [switch]$Execute,

    # Skip the "type the database name" confirmation prompt in -Execute mode.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

# --- 1. Resolve the connection string (explicit only) -------------------------
# Never read the app's user-secrets here: on this machine they can point at the
# PRODUCTION database, and a tool that deletes the ledger must not pick its
# target on its own.
if (-not $ConnectionString) {
    $ConnectionString = $env:LSM_PURGE_CONNECTION
}
if (-not $ConnectionString) {
    throw "No database given. Pass -ConnectionString '<connection string>' or set LSM_PURGE_CONNECTION. There is no default."
}

function Get-CsField([string]$cs, [string]$name) {
    $field = $cs -split ';' | Where-Object { $_ -match "^\s*$name\s*=" } | Select-Object -First 1
    if ($field) { return ($field -replace "^\s*$name\s*=\s*", '') }
    return ''
}
$dbName  = Get-CsField $ConnectionString 'Database'
$dbHost  = Get-CsField $ConnectionString 'Host'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK ('dotnet') was not found on PATH; it's required to run this tool."
}

Write-Host "Target DB : $dbHost / $dbName"
Write-Host ("Scope     : " + $(if ($LinkshellName) { "linkshell '$LinkshellName'" } else { "ALL linkshells" }))
Write-Host ("Mode      : " + $(if ($Execute) { "EXECUTE (will DELETE)" } else { "DRY RUN (counts only)" }))
Write-Host ("Balances  : " + $(if ($ResetBalances) { "reset member DKP (balance, seed, watermarks) to 0" } else { "left unchanged" }))
Write-Host ""

# --- 2. Confirmation guard when actually deleting -----------------------------
if ($Execute -and -not $Force) {
    Write-Host "This will PERMANENTLY DELETE rows from $dbHost/$dbName." -ForegroundColor Yellow
    $answer = Read-Host "Type the database name ('$dbName') to confirm"
    if ($answer -ne $dbName) {
        Write-Host "Confirmation did not match. Aborting; nothing was changed."
        exit 1
    }
}

# --- 3. Generate + run a throwaway .NET 10 tool that does the work ---------------
$work = Join-Path $env:TEMP ("lsm-purge-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
    <AssemblyName>lsmpurge</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Npgsql" Version="10.0.3" />
  </ItemGroup>
</Project>
'@ | Set-Content -Path (Join-Path $work 'purge.csproj') -Encoding UTF8

    @'
using Npgsql;

string conn = Environment.GetEnvironmentVariable("LSM_CONN")
    ?? throw new InvalidOperationException("LSM_CONN not set.");
bool execute = Environment.GetEnvironmentVariable("PURGE_EXECUTE") == "1";
bool resetBalances = Environment.GetEnvironmentVariable("PURGE_RESET_BALANCES") == "1";
string? linkshellName = Environment.GetEnvironmentVariable("PURGE_LINKSHELL");
if (string.IsNullOrWhiteSpace(linkshellName)) linkshellName = null;

await using var c = new NpgsqlConnection(conn);
await c.OpenAsync();
Console.WriteLine($"Connected to {c.DataSource}/{c.Database}.");

int? lsId = null;
if (linkshellName is not null)
{
    var ids = new List<int>();
    await using (var find = c.CreateCommand())
    {
        find.CommandText = "SELECT \"Id\" FROM \"Linkshells\" WHERE \"LinkshellName\" = @name";
        find.Parameters.AddWithValue("name", linkshellName);
        await using var r = await find.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetInt32(0));
    }
    if (ids.Count == 0) { Console.Error.WriteLine($"No linkshell named \"{linkshellName}\" found."); return 1; }
    if (ids.Count > 1) { Console.Error.WriteLine($"Multiple linkshells named \"{linkshellName}\" (ids {string.Join(", ", ids)})."); return 1; }
    lsId = ids[0];
    Console.WriteLine($"Scope: linkshell \"{linkshellName}\" (id {lsId}).");
}
else
{
    Console.WriteLine("Scope: ALL linkshells.");
}

// Build the WHERE clause per table. Rows scoped via EventHistory use a subquery.
string Where(bool viaEventHistory, string directCol)
{
    if (lsId is null) return "";
    return viaEventHistory
        ? " WHERE \"EventHistoryId\" IN (SELECT \"Id\" FROM \"EventHistories\" WHERE \"LinkshellId\" = @ls)"
        : $" WHERE \"{directCol}\" = @ls";
}

// Child-first order so each DELETE is safe regardless of FK ON DELETE behavior.
var targets = new (string Label, string Table, string Where)[]
{
    ("Job ratings",                       "JobRatings",             Where(false, "LinkshellId")),
    ("Event comments",                    "EventComments",          Where(true,  "")),
    ("DKP ledger entries",                "DkpLedgerEntries",       Where(false, "LinkshellId")),
    ("Past-event attendance rows",        "AppUserEventHistories",  Where(true,  "")),
    ("Past events (EventHistories)",      "EventHistories",         Where(false, "LinkshellId")),
};

async Task<long> CountAsync(NpgsqlTransaction? tx, string table, string where)
{
    await using var cmd = c.CreateCommand();
    if (tx != null) cmd.Transaction = tx;
    cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\"{where}";
    if (where.Contains("@ls")) cmd.Parameters.AddWithValue("ls", lsId!.Value);
    return (long)(await cmd.ExecuteScalarAsync())!;
}

Console.WriteLine(execute ? "\n=== MODE: EXECUTE (deleting) ===" : "\n=== MODE: DRY RUN (counts only) ===");

if (!execute)
{
    foreach (var t in targets)
    {
        var n = await CountAsync(null, t.Table, t.Where);
        Console.WriteLine($"  {t.Label,-32}: {n} row(s) would be deleted");
    }
    if (resetBalances)
    {
        var w = lsId is null ? "" : " WHERE \"LinkshellId\" = @ls";
        var n = await CountAsync(null, "AppUserLinkshells", w);
        Console.WriteLine($"  {"Member DKP balances -> 0",-32}: {n} membership row(s)");
    }
    Console.WriteLine("\nDRY RUN complete. Nothing was changed. Re-run with -Execute to apply.");
    return 0;
}

await using var tx = await c.BeginTransactionAsync();
try
{
    long total = 0;
    foreach (var t in targets)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"DELETE FROM \"{t.Table}\"{t.Where}";
        if (t.Where.Contains("@ls")) cmd.Parameters.AddWithValue("ls", lsId!.Value);
        var n = await cmd.ExecuteNonQueryAsync();
        total += n;
        Console.WriteLine($"  Deleted {n,6} from {t.Table} ({t.Label})");
    }
    if (resetBalances)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        var w = lsId is null ? "" : " WHERE \"LinkshellId\" = @ls";
        cmd.CommandText = $"UPDATE \"AppUserLinkshells\" SET \"LinkshellDkp\" = 0, \"SeededDkpEarned\" = 0, \"SeededDkpSpent\" = 0, \"DkpSeedLedgerId\" = 0, \"DkpPoolLedgerFromId\" = 0{w}";
        if (w.Contains("@ls")) cmd.Parameters.AddWithValue("ls", lsId!.Value);
        var n = await cmd.ExecuteNonQueryAsync();
        Console.WriteLine($"  Reset DKP (balance, seed, watermarks) to 0 on {n} membership row(s)");
    }
    await tx.CommitAsync();
    Console.WriteLine($"\nDONE. Committed. {total} row(s) deleted across {targets.Length} tables.");
    return 0;
}
catch (Exception ex)
{
    await tx.RollbackAsync();
    Console.Error.WriteLine($"FAILED, rolled back -- nothing was deleted: {ex.Message}");
    return 1;
}
'@ | Set-Content -Path (Join-Path $work 'Program.cs') -Encoding UTF8

    $env:LSM_CONN             = $ConnectionString
    $env:PURGE_EXECUTE        = $(if ($Execute) { '1' } else { '0' })
    $env:PURGE_RESET_BALANCES = $(if ($ResetBalances) { '1' } else { '0' })
    $env:PURGE_LINKSHELL      = $LinkshellName

    dotnet run --project $work -c Release --verbosity quiet
    $code = $LASTEXITCODE
}
finally {
    Remove-Item Env:LSM_CONN             -ErrorAction SilentlyContinue
    Remove-Item Env:PURGE_EXECUTE        -ErrorAction SilentlyContinue
    Remove-Item Env:PURGE_RESET_BALANCES -ErrorAction SilentlyContinue
    Remove-Item Env:PURGE_LINKSHELL      -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $work    -ErrorAction SilentlyContinue
}

exit $code
