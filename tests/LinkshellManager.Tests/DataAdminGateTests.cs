using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LinkshellManager.Tests;

// Two structural invariants of the Data Admin feature. (1) The gate: /data-admin is super-admin
// only, decided by the raw AppUser.IsSuperAdmin flag and nothing else -- not the admin.override
// toggle, not a linkshell rank. (2) The catalog is the ONLY place that enumerates the EF model:
// a second enumeration anywhere in the app would be a second way for a table to become reachable
// without passing through DataAdminPolicy, so this test scans the source for one.
public class DataAdminGateTests
{
    [Fact]
    public void Decide_ChallengesAnUnknownUser()
    {
        Assert.IsType<ChallengeResult>(SuperAdminOnlyAttribute.Decide(null));
    }

    [Fact]
    public void Decide_ForbidsAnOrdinaryUser_EvenALeader()
    {
        Assert.IsType<ForbidResult>(SuperAdminOnlyAttribute.Decide(new AppUser { UserName = "leader", IsSuperAdmin = false }));
    }

    [Fact]
    public void Decide_AdmitsASuperAdmin()
    {
        Assert.Null(SuperAdminOnlyAttribute.Decide(new AppUser { UserName = "millhouse", IsSuperAdmin = true }));
    }

    [Fact]
    public void OnlyTheCatalog_EnumeratesTheModel()
    {
        var offenders = new List<string>();
        foreach (var folder in new[] { "Controllers", "Services", "ViewModels", "Views", "Authorization", "Utils", "Data" })
        {
            foreach (var file in Directory.GetFiles(FindRepoPath(folder), "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var text = File.ReadAllText(file);
                var enumerates = text.Contains("GetEntityTypes(", StringComparison.Ordinal)
                    || text.Contains("typeof(ApplicationDbContext).GetProperties", StringComparison.Ordinal);
                if (enumerates && !file.EndsWith(Path.Combine("Services", "DataAdminCatalog.cs"), StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add(Path.GetFileName(file));
                }
            }
        }

        var repoRoot = Directory.GetParent(FindRepoPath("Data"))?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repo root.");
        if (File.ReadAllText(Path.Combine(repoRoot, "Program.cs")).Contains("GetEntityTypes(", StringComparison.Ordinal))
        {
            offenders.Add("Program.cs");
        }

        Assert.Empty(offenders);
    }

    /// <summary>Walks up from the test binary to the repo root, which holds the app's source folders.</summary>
    private static string FindRepoPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate '{relative}' above {AppContext.BaseDirectory}.");
    }
}
