using System;
using System.IO;
using System.Linq;
using Xunit;

namespace LinkshellManager.Tests;

// Opening a DKP page must never pay anyone.
//
// GET dkp-history (any member) and GET dkp-audit/add-candidates used to run
// WindowEventDkpLedgerService's linkshell-wide sweep, which appends missing SnapshotEarned rows
// for every posted camp. Its "already paid?" check is read-then-write, so a page reload landing
// while an officer pressed Post could credit the same camp twice. The controllers need a signed-in
// HTTP context to run, so this is pinned by source instead.
public class DkpReadsNeverPayTests
{
    [Fact]
    public void NoController_RunsTheLinkshellWideLedgerSweep()
    {
        var controllers = Path.Combine(FindRepoRoot(), "Controllers");
        var callers = Directory.GetFiles(controllers, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path)
                .Contains("EnsurePostedWindowEventLedgerEntriesForLinkshellAsync", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(
            callers.Count == 0,
            "These controllers run the linkshell-wide DKP sweep, which pays camps from a request "
            + $"that should only read: {string.Join(", ", callers)}");
    }

    [Fact]
    public void TheDkpPages_DoNotWriteTheLedger()
    {
        var path = Path.Combine(FindRepoRoot(), "Controllers", "Activity", "Dkp", "ActivityDkpController.Dkp.cs");
        Assert.True(File.Exists(path), "ActivityDkpController.Dkp.cs not found -- did it move?");
        var source = File.ReadAllText(path);

        // Neither the sweep nor the per-camp payout belongs in the DKP read file. (The manual audit
        // add/remove actions there write through _dkpLedger, which is an officer's explicit action.)
        Assert.DoesNotContain("EnsurePostedWindowEventLedgerEntries", source, StringComparison.Ordinal);
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
