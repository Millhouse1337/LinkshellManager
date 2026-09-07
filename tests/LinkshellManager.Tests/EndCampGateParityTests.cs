using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LinkshellManagerDiscordApp.Controllers;
using Xunit;

namespace LinkshellManager.Tests;

/// <summary>
/// End Camp / Post ToD is reachable from three places — the Discord board button (and the modal it
/// opens), the web signup board, and the Activity's live camp card — and for a long time they did
/// not agree on who may press it. The Activity endpoint checked the CanManageEvents PERMISSION
/// while the other two checked the Leader/Officer RANK, so a linkshell that built a custom role
/// with "Manage events" on got a role that could end a camp in the Activity and nowhere else, and
/// whose button the Activity itself never drew.
///
/// A coarse rank on one surface and a named permission on another is the ChartsWriteGateParityTests
/// hazard in a more expensive place: ending a camp finalizes DKP and tears the board down. These
/// pin the SHAPE the way that file does — reflection cannot prove a call site, so they assert the
/// gate exists and that no End Camp path still reaches for the rank check.
/// </summary>
public class EndCampGateParityTests
{
    // The three server files that gate an End Camp path, named rather than counted so adding a
    // fourth surface is a deliberate edit here instead of a silently-passing test.
    private static readonly string[] EndCampGateFiles =
    {
        "DiscordInteractionsController.cs",
        "EventController.HnmBoard.cs",
        "ActivityDataController.HnmBoard.cs",
    };

    /// <summary>
    /// Both rank-gated surfaces grew their own CanManageEventsAsync, mirroring the order
    /// ActivityDataController.CanAsync already used: membership, override, Leader-always-wins,
    /// then the role row.
    /// </summary>
    [Fact]
    public void BothRankGatedControllersExposeAnEventPermissionGate()
    {
        Assert.NotNull(Method(typeof(DiscordInteractionsController), "CanManageEventsAsync"));
        Assert.NotNull(Method(typeof(EventController), "CanManageEventsAsync"));
    }

    /// <summary>
    /// Each surface's gate is still the coarse rank check as well, kept as a separate method: the
    /// permission gate must not quietly become the rank gate under a new name, which is exactly
    /// how the two would drift back together.
    /// </summary>
    [Fact]
    public void TheCoarseRankGateSurvivesAsItsOwnMethod()
    {
        Assert.NotNull(Method(typeof(DiscordInteractionsController), "CanManageLinkshellAsync"));
        Assert.NotNull(Method(typeof(EventController), "CanManageLinkshellAsync"));
    }

    /// <summary>
    /// Every End Camp gate names CanManageEvents. A surface that stopped would be back to deciding
    /// this by rank, and the mismatch is invisible until a linkshell builds the custom role.
    /// </summary>
    [Fact]
    public void EveryEndCampSurfaceGatesOnTheEventPermission()
    {
        foreach (var name in EndCampGateFiles)
        {
            var text = ControllerSource(name);
            Assert.True(
                text.Contains("CanManageEvents", StringComparison.Ordinal),
                $"{name} no longer gates on CanManageEvents — End Camp is decided by permission, not rank.");
        }
    }

    /// <summary>
    /// The End Camp handlers themselves must not call the rank gate.
    ///
    /// DiscordInteractionsController.cs is excluded from the "no rank gate anywhere" form of this
    /// check because it legitimately rank-gates its OTHER officer buttons (Add / Remove / Move
    /// Member, Set Leader). So this asserts the narrower, checkable thing: neither camp-ending
    /// path still answers "only officers can end the camp", the message the rank gate returned.
    /// </summary>
    [Fact]
    public void NoEndCampPathStillRefusesOnRank()
    {
        foreach (var (file, text) in ControllerSources())
        {
            Assert.False(
                text.Contains("Only officers can end the camp", StringComparison.OrdinalIgnoreCase),
                $"{Path.GetFileName(file)} still refuses End Camp on rank — gate it on CanManageEvents.");
            Assert.False(
                text.Contains("Leader or officer access is required to log a Time of Death", StringComparison.OrdinalIgnoreCase),
                $"{Path.GetFileName(file)} still refuses the web board's ToD on rank — gate it on CanManageEvents.");
        }
    }

    /// <summary>
    /// The Activity's End Camp BUTTON is drawn off the same permission its endpoint checks.
    ///
    /// This is the half that was wrong in the other direction: the endpoint always checked
    /// CanManageEvents, but the card gated the button on canManageLinkshellIn(), so the role the
    /// server would have accepted was never offered the control.
    /// </summary>
    [Fact]
    public void TheActivityCardDrawsEndCampOffThePermission()
    {
        var template = File.ReadAllText(
            Path.Combine(ActivityTabsPath(), "events-tab.component.html"));
        var component = File.ReadAllText(
            Path.Combine(ActivityTabsPath(), "events-tab.component.ts"));

        Assert.Contains("canEndCamp(event.linkshellId)", template, StringComparison.Ordinal);
        Assert.Contains("canManageEventsIn", component, StringComparison.Ordinal);
    }

    private static MethodInfo? Method(Type type, string name) =>
        type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance);

    private static string ControllerSource(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), fileName));

    private static (string File, string Text)[] ControllerSources() =>
        Directory.GetFiles(FindRepoPath("Controllers"), "*.cs", SearchOption.AllDirectories)
            .Select(file => (file, File.ReadAllText(file)))
            .ToArray();

    private static string ActivityTabsPath() =>
        FindRepoPath(Path.Combine("discord-activity", "src", "app", "home", "tabs"));

    /// <summary>Walks up from the test binary to the repo root. Same shape as ChartsWriteGateParityTests.</summary>
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
