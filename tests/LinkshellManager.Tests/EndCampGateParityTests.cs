using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers.Discord;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

/// <summary>
/// End Camp / Post ToD is reachable from two places — the Discord board button (and the modal it
/// opens) and the Activity's live camp card (the old website's signup board was a third, until it
/// was deleted) — and for a long time they did not agree on who may press it. The Activity endpoint
/// checked the CanManageEvents PERMISSION while the others checked the Leader/Officer RANK, so a
/// linkshell that built a custom role with "Manage events" on got a role that could end a camp in
/// the Activity and nowhere else, and whose button the Activity itself never drew.
///
/// A coarse rank on one surface and a named permission on another is the ChartsWriteGateParityTests
/// hazard in a more expensive place: ending a camp finalizes DKP and tears the board down. These
/// pin the SHAPE the way that file does — reflection cannot prove a call site, so they assert the
/// gate exists and that no End Camp path still reaches for the rank check.
/// </summary>
public class EndCampGateParityTests
{
    // The server files that gate an End Camp path, named rather than counted so adding a
    // surface is a deliberate edit here instead of a silently-passing test.
    private static readonly string[] EndCampGateFiles =
    {
        Path.Combine("Discord", "DiscordPermissionGates.cs"),
        Path.Combine("Activity", "WindowEvents", "ActivityWindowEventsController.HnmBoard.cs"),
    };

    /// <summary>
    /// The Discord surface's End Camp gate. It asks LinkshellPermissions, the same service the
    /// Activity's CanAsync uses, so the two cannot drift apart again.
    /// </summary>
    [Fact]
    public void TheDiscordControllerExposesAnEventPermissionGate()
    {
        Assert.NotNull(Method(typeof(DiscordPermissionGates), "CanEndCampAsync"));
    }

    /// <summary>
    /// The Discord controller keeps no permission rule of its own any more: no rank test, no
    /// role-row lookup, no direct override check. Its old rank gates (IsEventOfficerAsync,
    /// CanManageLinkshellAsync) and its own copy of the role rule (CanManageEventsAsync) are gone;
    /// a copy that came back is how the surfaces drifted apart in the first place.
    /// </summary>
    [Fact]
    public void TheDiscordControllerHasNoPermissionRuleOfItsOwn()
    {
        // The controller and the handler classes split out of it (Controllers/Discord).
        var text = DiscordBotSource();
        Assert.DoesNotContain("IsLeaderOrOfficer", text);
        Assert.DoesNotContain("LinkshellRanks.IsLeader", text);
        Assert.DoesNotContain("IsEventOfficerAsync", text);
        Assert.DoesNotContain("CanManageLinkshellAsync", text);
        Assert.DoesNotContain("CanManageEventsAsync", text);
        Assert.DoesNotContain("AdminOverrideService", text);
        Assert.False(text.Contains("LinkshellRoles", StringComparison.Ordinal),
            "The Discord bot reads LinkshellRoles itself; ask LinkshellPermissions instead.");
    }

    /// <summary>
    /// The custom role this file was written about: a role with "Manage events" on may end a camp
    /// from the Discord board, and the same role with it off may not, exactly as in the Activity.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscordEndCamp_CustomRole_ManageEventsDecides(bool flag)
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "LS1", LootStructure = "Dkp" });
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(1));
        db.LinkshellRoles.Add(new LinkshellRole { LinkshellId = 1, Name = "Camp Lead", CanManageEvents = flag });
        db.Users.Add(new AppUser { Id = "u1", UserName = "u1" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { AppUserId = "u1", LinkshellId = 1, Rank = "Camp Lead" });
        await db.SaveChangesAsync();

        var gates = new DiscordPermissionGates(db, TestPermissions.For(db));

        Assert.Equal(flag, await gates.CanEndCampAsync("u1", 1, CancellationToken.None));
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
    /// The End Camp handlers themselves must not call the rank gate: no camp-ending path still
    /// answers "only officers can end the camp", the message the rank gate returned.
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

    private static string DiscordBotSource() =>
        string.Join("\n", new[] { Path.Combine(FindRepoPath("Controllers"), "DiscordInteractionsController.cs") }
            .Concat(Directory.GetFiles(Path.Combine(FindRepoPath("Controllers"), "Discord"), "*.cs"))
            .Select(File.ReadAllText));

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
