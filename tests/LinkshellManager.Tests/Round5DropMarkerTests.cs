using LinkshellManagerDiscordApp.Migrations;
using Xunit;

namespace LinkshellManager.Tests;

// Round 5: R5.6 marked every piece of app code that R5.7 deletes with "// ROUND5-DROP", and
// docs/round5-drop-manifest.md listed it. R5.7 (Round5Contract) deleted the marked code and dropped
// the 15 columns. These keep the two done and in step: no marker is left in the app, and the
// manifest names every dropped column and holds the rollback SQL word for word (the SQL the owner
// runs in psql before swapping back to update B; Round5ContractRoundTripTests runs the same text).
public class Round5DropMarkerTests
{
    private const string Marker = "ROUND5-DROP";

    // The 14 old account columns (the 15th dropped column is the roster name).
    private static readonly string[] AccountColumns =
    {
        "JobLevels", "StrongJobs", "MeritJobs", "CraftLevels",
        "AltCharacterName1", "Alt1JobLevels", "Alt1StrongJobs", "Alt1MeritJobs", "Alt1CraftLevels",
        "AltCharacterName2", "Alt2JobLevels", "Alt2StrongJobs", "Alt2MeritJobs", "Alt2CraftLevels",
    };

    [Fact]
    public void NoMarkerIsLeft_InTheApp()
    {
        var root = FindRepoRoot();
        var found = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith("tests/", StringComparison.Ordinal)
                && !path.Contains("/bin/", StringComparison.Ordinal)
                && !path.Contains("/obj/", StringComparison.Ordinal)
                && !path.StartsWith("bin/", StringComparison.Ordinal)
                && !path.StartsWith("obj/", StringComparison.Ordinal)
                && !path.Contains("node_modules", StringComparison.Ordinal))
            .Where(path => File.ReadLines(Path.Combine(root, path)).Any(line => line.Contains(Marker, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(found);
    }

    [Fact]
    public void TheManifest_NamesEveryColumn_AndHoldsTheRollbackSqlWordForWord()
    {
        var manifest = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "round5-drop-manifest.md")).Replace("\r\n", "\n");
        foreach (var column in AccountColumns)
        {
            Assert.Contains($"| `AspNetUsers` | `{column}` |", manifest, StringComparison.Ordinal);
        }
        Assert.Contains("| `AppUserLinkshells` | `CharacterName` |", manifest, StringComparison.Ordinal);
        Assert.Contains("R5.7 DONE", manifest, StringComparison.Ordinal);

        // The rollback SQL, as one ```sql block, exactly the migration's text.
        var rollback = Round5Contract.RollbackSql.Replace("\r\n", "\n");
        Assert.Contains("```sql\n" + rollback + "\n```", manifest, StringComparison.Ordinal);
        Assert.Contains(Round5Contract.MigrationId, manifest, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Controllers")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate the repo root above {AppContext.BaseDirectory}.");
    }
}
