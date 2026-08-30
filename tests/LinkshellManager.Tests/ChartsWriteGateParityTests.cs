using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LinkshellManagerDiscordApp.Controllers;
using Xunit;

namespace LinkshellManager.Tests;

/// <summary>
/// The wishlist and key items are the first Charts writes open to a member WITHOUT CanManageCharts,
/// which makes "who may do this" a decision two front-ends now have to agree on.
///
/// A coarse check on one surface and a named permission on the other is a privilege escalation
/// available by picking a front-end. That is the bug GrantTreasuryToOfficersWhoUsedIt exists to
/// document, and it is why ChartsController checks CanManageCharts rather than a rank.
///
/// Reflection cannot prove a CALL SITE, so these pin the SHAPE instead: both controllers expose the
/// member gate, and neither holds a private copy of an ownership comparison. Precedent for reading
/// the repo off disk in a unit test: ChartBoardCatalogTests.EveryBossEmblem_ExistsOnDisk.
/// </summary>
public class ChartsWriteGateParityTests
{
    private static string ControllersPath() => FindRepoPath("Controllers");

    [Fact]
    public void BothControllersExposeAMemberLevelGate()
    {
        Assert.NotNull(Method(typeof(ChartsController), "AuthorizeMemberAsync"));
        Assert.NotNull(Method(typeof(ActivityDataController), "AuthorizeChartsMemberAsync"));
    }

    // Both must still keep the OFFICER gate as a separate thing: a member gate that quietly returned
    // true for officers only would be indistinguishable at the call site.
    [Fact]
    public void BothControllersStillExposeTheOfficerGate()
    {
        Assert.NotNull(Method(typeof(ChartsController), "AuthorizeWriteAsync"));
        Assert.NotNull(Method(typeof(ActivityDataController), "AuthorizeChartsWriteAsync"));
    }

    /// <summary>
    /// No controller compares the requester id itself.
    ///
    /// Ownership is ChartWishlistService.CanEditRequest, in one place. A controller that wrote
    /// `row.RequestedByAppUserId == user.Id` would be a second copy - it would look right, and it
    /// would quietly drift the day the rule changes (an unsynced requester, say, whose id is null on
    /// both sides). Assignment is fine; comparison is not.
    /// </summary>
    [Fact]
    public void NoControllerComparesTheRequesterIdItself()
    {
        foreach (var (file, text) in ControllerSources())
        {
            Assert.DoesNotContain("RequestedByAppUserId ==", text);
            Assert.DoesNotContain("RequestedByAppUserId !=", text);
            Assert.False(
                text.Contains("Equals(row.RequestedByAppUserId", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} compares RequestedByAppUserId directly - "
                    + "call ChartWishlistService.CanEditRequest instead.");
        }
    }

    /// <summary>Twin of the above for the key item rule, which has the same two-surface hazard.</summary>
    [Fact]
    public void NoControllerDecidesKeyItemOwnershipItself()
    {
        var callers = ControllerSources()
            .Where(source => source.Text.Contains("ChartKeyItemService.CanSetKeyItemFor", StringComparison.Ordinal))
            .Select(source => Path.GetFileName(source.File))
            .ToList();

        // Exactly the two write paths, one per surface. A third would be a copy; a missing one would
        // be an ungated endpoint.
        Assert.Contains("ChartsController.cs", callers);
        Assert.Contains("ActivityDataController.ChartsKeyItems.cs", callers);
    }

    /// <summary>
    /// Both wishlist write paths reach the shared rule. Named files rather than a count, so adding a
    /// surface is a deliberate edit here rather than a silently-passing test.
    /// </summary>
    [Fact]
    public void BothSurfacesCallTheSharedOwnershipRule()
    {
        var callers = ControllerSources()
            .Where(source => source.Text.Contains("ChartWishlistService.CanEditRequest", StringComparison.Ordinal))
            .Select(source => Path.GetFileName(source.File))
            .ToList();

        Assert.Contains("ChartsController.cs", callers);
        Assert.Contains("ActivityDataController.ChartsWishlist.cs", callers);
    }

    private static MethodInfo? Method(Type type, string name) =>
        type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);

    private static (string File, string Text)[] ControllerSources() =>
        Directory.GetFiles(ControllersPath(), "*.cs", SearchOption.AllDirectories)
            .Select(file => (file, File.ReadAllText(file)))
            .ToArray();

    /// <summary>Walks up from the test binary to the repo root, which holds the app's Controllers.</summary>
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
