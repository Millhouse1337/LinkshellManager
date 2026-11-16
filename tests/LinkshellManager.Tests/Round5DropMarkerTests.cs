using System.Text.RegularExpressions;
using Xunit;

namespace LinkshellManager.Tests;

// Round 5, R5.6: every piece of app code that R5.7 deletes carries a "// ROUND5-DROP" marker, and
// docs/round5-drop-manifest.md lists them. This keeps the two in step: the markers sit exactly where
// the manifest says, on exactly the 15 columns it drops, and the manifest names every one.
// (R5.7 deletes the marked code; then this test goes too.)
public class Round5DropMarkerTests
{
    private const string Marker = "ROUND5-DROP";

    // The 14 old account columns and the roster name.
    private static readonly string[] AccountColumns =
    {
        "JobLevels", "StrongJobs", "MeritJobs", "CraftLevels",
        "AltCharacterName1", "Alt1JobLevels", "Alt1StrongJobs", "Alt1MeritJobs", "Alt1CraftLevels",
        "AltCharacterName2", "Alt2JobLevels", "Alt2StrongJobs", "Alt2MeritJobs", "Alt2CraftLevels",
    };

    private static readonly Dictionary<string, int> ExpectedMarkers = new(StringComparer.Ordinal)
    {
        ["Models/AppUser.cs"] = 14,
        ["Models/AppUserLinkshell.cs"] = 1,
        ["Data/ApplicationDbContext.AccountNames.cs"] = 2,
        ["Data/CharacterSync.cs"] = 2,
        ["Services/DataAdminPolicy.cs"] = 1,
    };

    [Fact]
    public void Markers_AreExactlyWhereTheManifestSays()
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
            .Select(path => (path, count: File.ReadLines(Path.Combine(root, path)).Count(line => line.Contains(Marker, StringComparison.Ordinal))))
            .Where(file => file.count > 0)
            .ToDictionary(file => file.path, file => file.count, StringComparer.Ordinal);

        Assert.Equal(
            ExpectedMarkers.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            found.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    // Each model marker sits right above one of the 15 dropped properties (attributes between).
    [Fact]
    public void ModelMarkers_AreOnExactlyTheDroppedColumns()
    {
        var root = FindRepoRoot();
        Assert.Equal(
            AccountColumns.OrderBy(name => name, StringComparer.Ordinal),
            MarkedProperties(Path.Combine(root, "Models", "AppUser.cs")).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(new[] { "CharacterName" }, MarkedProperties(Path.Combine(root, "Models", "AppUserLinkshell.cs")));
    }

    [Fact]
    public void TheManifest_NamesEveryColumnAndEveryMarkedFile()
    {
        var manifest = File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "round5-drop-manifest.md"));
        foreach (var column in AccountColumns)
        {
            Assert.Contains($"| `AspNetUsers` | `{column}` |", manifest, StringComparison.Ordinal);
        }
        Assert.Contains("| `AppUserLinkshells` | `CharacterName` |", manifest, StringComparison.Ordinal);
        foreach (var file in ExpectedMarkers.Keys)
        {
            Assert.Contains($"`{file}`", manifest, StringComparison.Ordinal);
        }
        // The SQL to go back further than update B (AccountNameStampTests runs it word for word).
        Assert.Contains("### Mirror refill", manifest, StringComparison.Ordinal);
    }

    private static List<string> MarkedProperties(string path)
    {
        var lines = File.ReadAllLines(path);
        var names = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(Marker, StringComparison.Ordinal))
            {
                continue;
            }
            var next = i + 1;
            while (next < lines.Length && lines[next].TrimStart().StartsWith('['))
            {
                next++;
            }
            var match = Regex.Match(lines[next], @"^\s*public\s+\S+\s+(\w+)\s*\{\s*get;\s*set;\s*\}");
            Assert.True(match.Success, $"{Path.GetFileName(path)}:{i + 1}: the marker is not above a property.");
            names.Add(match.Groups[1].Value);
        }
        return names;
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
