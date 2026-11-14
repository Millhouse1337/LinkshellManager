using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Writes EVERY row of EVERY table as plain text, so a test can compare the whole database against
// a saved copy (a "golden file") and see any row or column that changed.
//
// Why: End Camp touches a dozen tables at once (the review card, its captures, the Past Event, the
// windows, Claim Shield captures, loot, the ledger, balances, the board). Pinning that by hand
// would miss whatever nobody thought to assert. A full dump misses nothing.
//
// Times are written relative to the test's start ("T0+30m") so the file is the same on every run.
// Row versions (xmin) are left out: they are Postgres transaction numbers, not data.
internal static class DatabaseStateDump
{
    // Columns whose last digits depend on how long the test took (an hour count worked out from
    // "now"). Rounded to 2 decimals so a slow machine still matches.
    private static readonly HashSet<string> ElapsedHourColumns = new(StringComparer.Ordinal) { "Duration" };

    // Random on every run (Identity fills them with a new Guid) and nothing End Camp touches.
    private static readonly HashSet<string> RandomColumns = new(StringComparer.Ordinal) { "SecurityStamp", "ConcurrencyStamp" };

    public static async Task<string> DumpAsync(ApplicationDbContext db, DateTime t0)
    {
        db.ChangeTracker.Clear();
        var text = new StringBuilder();

        var tables = db.Model.GetEntityTypes()
            .Where(type => type.BaseType is null
                && !type.IsOwned()
                && type.FindPrimaryKey() is not null
                && type.GetTableName() is not null
                && !type.HasSharedClrType)
            .OrderBy(type => type.GetTableName(), StringComparer.Ordinal);

        foreach (var type in tables)
        {
            var rows = await LoadRowsAsync(db, type);
            if (rows.Count == 0)
            {
                continue;
            }

            text.Append("[").Append(type.GetTableName()).Append("] ").Append(rows.Count).Append(" row(s)\n");
            var key = type.FindPrimaryKey()!.Properties;
            var described = rows
                .Select(row => (Key: KeyOf(db, row, key), Line: Describe(db, row, t0)))
                .OrderBy(pair => pair.Key, KeyComparer.Instance)
                .Select(pair => pair.Line);
            foreach (var line in described)
            {
                text.Append("  ").Append(line).Append('\n');
            }
        }

        db.ChangeTracker.Clear();
        return text.ToString();
    }

    // Compares `actual` with the saved file, or rewrites the file when UPDATE_END_CAMP_SNAPSHOT=1.
    //
    // Regenerate (only when a change in behaviour is MEANT, and review the diff before committing):
    //   PowerShell:  $env:UPDATE_END_CAMP_SNAPSHOT=1; dotnet test tests/LinkshellManager.PostgresTests --filter EndCampCharacterizationTests; Remove-Item Env:UPDATE_END_CAMP_SNAPSHOT
    public static void AssertMatchesSnapshot(string snapshotName, string actual)
    {
        var path = Path.Combine(FindSnapshotsFolder(), snapshotName + ".txt");
        if (Environment.GetEnvironmentVariable("UPDATE_END_CAMP_SNAPSHOT") == "1")
        {
            File.WriteAllText(path, actual.Replace("\r\n", "\n"));
            return;
        }

        Assert.True(File.Exists(path), $"No snapshot at {path}. Run once with UPDATE_END_CAMP_SNAPSHOT=1 to write it.");
        var expectedLines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
        var actualLines = actual.Replace("\r\n", "\n").Split('\n');
        if (expectedLines.SequenceEqual(actualLines, StringComparer.Ordinal))
        {
            return;
        }

        var report = new StringBuilder();
        report.Append("The database after End Camp no longer matches Snapshots/EndCamp/")
            .Append(snapshotName).Append(".txt (- expected, + actual):\n");
        var shown = 0;
        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length) && shown < 40; i++)
        {
            var expected = i < expectedLines.Length ? expectedLines[i] : "<missing>";
            var got = i < actualLines.Length ? actualLines[i] : "<missing>";
            if (string.Equals(expected, got, StringComparison.Ordinal))
            {
                continue;
            }
            report.Append("line ").Append(i + 1).Append('\n')
                .Append("- ").Append(expected).Append('\n')
                .Append("+ ").Append(got).Append('\n');
            shown++;
        }
        Assert.Fail(report.ToString());
    }

    private static async Task<List<object>> LoadRowsAsync(ApplicationDbContext db, IEntityType type)
    {
        var method = typeof(DatabaseStateDump)
            .GetMethod(nameof(LoadRowsOfAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(type.ClrType);
        return await (Task<List<object>>)method.Invoke(null, new object[] { db })!;
    }

    // Tracked (not AsNoTracking) so shadow columns can be read through the change tracker.
    private static async Task<List<object>> LoadRowsOfAsync<T>(ApplicationDbContext db) where T : class
        => (await db.Set<T>().IgnoreQueryFilters().ToListAsync()).Cast<object>().ToList();

    private static object?[] KeyOf(ApplicationDbContext db, object row, IReadOnlyList<IProperty> key)
    {
        var entry = db.Entry(row);
        return key.Select(property => entry.Property(property.Name).CurrentValue).ToArray();
    }

    private static string Describe(ApplicationDbContext db, object row, DateTime t0)
    {
        var entry = db.Entry(row);
        var type = entry.Metadata;
        var columns = type.GetProperties()
            .Where(property => !property.IsConcurrencyToken)
            .Select(property => property.Name + "=" + Format(property.Name, entry.Property(property.Name).CurrentValue, t0));
        var prefix = type.ClrType == type.GetRootType().ClrType ? string.Empty : type.ClrType.Name + " ";
        return prefix + string.Join(" | ", columns);
    }

    private static string Format(string column, object? value, DateTime t0) => value switch
    {
        null => "null",
        _ when RandomColumns.Contains(column) => "<random>",
        string text => "\"" + text.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r") + "\"",
        DateTime moment => Relative(moment, t0),
        DateTimeOffset offset => Relative(offset.UtcDateTime, t0),
        double number when ElapsedHourColumns.Contains(column) => Math.Round(number, 2).ToString("0.00", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable items => "[" + string.Join(",", items.Cast<object?>().Select(item => Format(column, item, t0))) + "]",
        _ => value.ToString() ?? "?",
    };

    // Minutes from the start of the test. End Camp stamps "now" a second or two after T0, which
    // reads as T0+0m every time.
    //
    // A column nobody set (0001-01-01) is written as it is, since it is not "some minutes ago".
    private static string Relative(DateTime value, DateTime t0)
    {
        if (value.Year < 2000)
        {
            return value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        var minutes = (long)Math.Round((DateTime.SpecifyKind(value, DateTimeKind.Utc) - t0).TotalMinutes);
        return minutes >= 0 ? $"T0+{minutes}m" : $"T0-{-minutes}m";
    }

    private static string FindSnapshotsFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var project = Path.Combine(directory.FullName, "tests", "LinkshellManager.PostgresTests");
            if (Directory.Exists(project))
            {
                var snapshots = Path.Combine(project, "Snapshots", "EndCamp");
                Directory.CreateDirectory(snapshots);
                return snapshots;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate tests/LinkshellManager.PostgresTests above {AppContext.BaseDirectory}.");
    }

    // Sorts rows by primary key: numbers as numbers, everything else as text.
    private sealed class KeyComparer : IComparer<object?[]>
    {
        public static readonly KeyComparer Instance = new();

        public int Compare(object?[]? x, object?[]? y)
        {
            for (var i = 0; i < Math.Min(x!.Length, y!.Length); i++)
            {
                var result = (x[i], y[i]) switch
                {
                    (null, null) => 0,
                    (null, _) => -1,
                    (_, null) => 1,
                    (IComparable a, var b) when a.GetType() == b.GetType() => a.CompareTo(b),
                    var (a, b) => string.CompareOrdinal(a.ToString(), b.ToString()),
                };
                if (result != 0)
                {
                    return result;
                }
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
