using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// How a camp posts once its monster's Window Method is stamped on it (DiscordEventMessageBuilder's
// WindowMethodFor family), and the two promises that make the setting safe to ship:
//   * every built-in monster, stamped with its default method, posts exactly as it did unstamped;
//   * Manual Check In camps and non-HNM events ignore the method.
public class WindowMethodTests
{
    private static readonly DateTime CampStart = new(2026, 10, 28, 18, 0, 0, DateTimeKind.Utc);

    // A Standard-mode HNM camp shaped the way HnmEventSeeder files one from the app.
    private static Event Camp(
        string monster, string? windowMethod, int? spawnWindows = null, int? spawnMinutes = null,
        int? windowCountOverride = null, string? attendanceMode = null, string eventType = "HNM") => new()
    {
        Id = 1,
        EventName = monster,
        EventType = eventType,
        AssignedMonsterName = monster,
        StartTime = CampStart,
        CommencementStartTime = CampStart,
        HnmWindowNumber = 1,
        AttendanceMode = attendanceMode,
        WindowCountOverride = windowCountOverride,
        SpawnWindowCount = spawnWindows,
        SpawnWindowMinutes = spawnMinutes,
        WindowMethod = windowMethod,
    };

    public static IEnumerable<object[]> BuiltInMonsters()
    {
        var names = HnmConfig.CombinedMonsterOptions(TodCatalog.SupportedMonsters)
            .Concat(new[] { "Bune", "Boroka", "Roc", "Capricious Cassie" })
            .Concat(HnmConfig.TestingHnms);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Select(name => new object[] { name });
    }

    // THE parity promise. A built-in monster's camp, stamped with the method its row defaults to,
    // must name, count and close its windows exactly as the same camp did before the column existed.
    [Theory]
    [MemberData(nameof(BuiltInMonsters))]
    public void BuiltInMonster_StampedWithItsDefault_PostsExactlyAsBefore(string monster)
    {
        var cadence = HnmConfig.DefaultWindowCadence(monster);
        Event Seeded(string? method) => Camp(
            monster, method, cadence?.Windows, cadence?.Minutes, HnmConfig.EffectiveWindowCount(monster));

        var legacy = Seeded(null);
        var stamped = Seeded(MonsterTimingDefaults.DefaultWindowMethod(monster));

        Assert.Equal(DiscordEventMessageBuilder.AttendancePostCount(legacy), DiscordEventMessageBuilder.AttendancePostCount(stamped));
        Assert.Equal(DiscordEventMessageBuilder.EffectiveWindowCount(legacy), DiscordEventMessageBuilder.EffectiveWindowCount(stamped));
        Assert.Equal(DiscordEventMessageBuilder.PostsOpenAndClose(legacy), DiscordEventMessageBuilder.PostsOpenAndClose(stamped));
        Assert.Equal(DiscordEventMessageBuilder.CloseIsLatestPost(legacy), DiscordEventMessageBuilder.CloseIsLatestPost(stamped));
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            Assert.Equal(
                DiscordEventMessageBuilder.DefaultWindowLabel(legacy, sequence),
                DiscordEventMessageBuilder.DefaultWindowLabel(stamped, sequence));
        }
    }

    // The case this whole setting exists for: a custom NM with no window grid used to be one post
    // paid by duration. Open/Close gives it an Open, a Close and a Kill.
    [Fact]
    public void OpenClose_WithNoWindowGrid_TakesAnOpenACloseAndAKill()
    {
        var camp = Camp("Jugner Funguar", MonsterWindowMethods.OpenClose, windowCountOverride: 1);

        Assert.Equal(1, DiscordEventMessageBuilder.EffectiveWindowCount(camp));
        Assert.Equal(2, DiscordEventMessageBuilder.AttendancePostCount(camp));
        Assert.True(DiscordEventMessageBuilder.PostsOpenAndClose(camp));
        Assert.True(DiscordEventMessageBuilder.CloseIsLatestPost(camp));
        Assert.Equal(2, DiscordEventMessageBuilder.MaxPostSequence(camp));
        Assert.Equal(3, DiscordEventMessageBuilder.KillPostCeiling(camp));
        Assert.Equal("Open", DiscordEventMessageBuilder.DefaultWindowLabel(camp, 1));
        Assert.Equal("Close", DiscordEventMessageBuilder.DefaultWindowLabel(camp, 2));
        Assert.Equal("Kill", DiscordEventMessageBuilder.DefaultWindowLabel(camp, 3));
    }

    // A stamped camp reads its Monster Setups grid, even when a legacy override of 1 sits beside it.
    [Fact]
    public void OpenClose_WithAGrid_SitsThroughItsSpawnWindowsButPostsTwice()
    {
        var camp = Camp("Jugner Funguar", MonsterWindowMethods.OpenClose, 7, 10, windowCountOverride: 1);

        Assert.Equal(7, DiscordEventMessageBuilder.EffectiveWindowCount(camp));
        Assert.Equal(2, DiscordEventMessageBuilder.AttendancePostCount(camp));
        Assert.Equal(2, DiscordEventMessageBuilder.MaxPostSequence(camp));
    }

    [Fact]
    public void WindowN_WithAGrid_PostsEveryWindow_Numbered()
    {
        var camp = Camp("Jugner Funguar", MonsterWindowMethods.WindowN, 7, 10);

        Assert.Equal(7, DiscordEventMessageBuilder.AttendancePostCount(camp));
        Assert.False(DiscordEventMessageBuilder.PostsOpenAndClose(camp));
        Assert.False(DiscordEventMessageBuilder.WindowsOpenEnded(camp));
        Assert.False(DiscordEventMessageBuilder.CloseIsLatestPost(camp));
        Assert.Equal(7, DiscordEventMessageBuilder.MaxPostSequence(camp));
        Assert.Equal(8, DiscordEventMessageBuilder.KillPostCeiling(camp));
        Assert.Null(DiscordEventMessageBuilder.DefaultWindowLabel(camp, 1));
    }

    // Two spawn windows is NOT an Open and a Close under Window N. "post count == 2" used to be
    // exactly how a camp was recognised as Open/Close, which is why that test had to go.
    [Fact]
    public void WindowN_WithTwoWindows_StaysNumbered()
    {
        var camp = Camp("Jugner Funguar", MonsterWindowMethods.WindowN, 2, 10);

        Assert.Equal(2, DiscordEventMessageBuilder.AttendancePostCount(camp));
        Assert.False(DiscordEventMessageBuilder.PostsOpenAndClose(camp));
        Assert.Null(DiscordEventMessageBuilder.DefaultWindowLabel(camp, 2));
    }

    [Fact]
    public void WindowN_WithNoWindowGrid_IsOpenEnded()
    {
        var camp = Camp("King Vinegarroon", MonsterWindowMethods.WindowN, windowCountOverride: 1);

        Assert.True(DiscordEventMessageBuilder.WindowsOpenEnded(camp));
        Assert.Equal(HnmConfig.MaxWindow, DiscordEventMessageBuilder.AttendancePostCount(camp));
        Assert.True(DiscordEventMessageBuilder.CloseIsLatestPost(camp));
        Assert.Equal(HnmConfig.MaxWindow, DiscordEventMessageBuilder.MaxPostSequence(camp));
        Assert.Equal(HnmConfig.MaxWindow + 1, DiscordEventMessageBuilder.KillPostCeiling(camp));
    }

    // The open-ended camp's spawn counter never leaves 1, so the close is its latest post.
    [Fact]
    public void OpenEndedCamp_ClosesOnItsLatestPost()
    {
        var posted = new[] { 1, 2, 3, 4 };
        Assert.Equal(4, HnmStandardCampFinalizer.ResolveCloseWindow(
            posted, popWindow: 1, markedCloseWindow: null, closeIsLatestPost: true));
    }

    // A camp with no stamp keeps the range it was always checked against: its spawn count.
    [Fact]
    public void UnstampedCamp_KeepsItsSpawnCountAsThePostRange()
    {
        var king = Camp("Behemoth/King Behemoth", null, 7, 10, windowCountOverride: 7);

        Assert.Equal(7, DiscordEventMessageBuilder.MaxPostSequence(king));
        Assert.Equal(8, DiscordEventMessageBuilder.KillPostCeiling(king));
    }

    // Manual Check In is paid from check-ins, so a stamped method must not reshape it.
    [Fact]
    public void ManualCheckIn_IgnoresTheMethod()
    {
        var king = Camp("Behemoth/King Behemoth", MonsterWindowMethods.WindowN, 7, 10, 7, HnmAttendanceModes.Wd);

        Assert.Null(DiscordEventMessageBuilder.WindowMethodFor(king));
        Assert.Equal(2, DiscordEventMessageBuilder.AttendancePostCount(king));
        Assert.True(DiscordEventMessageBuilder.PostsOpenAndClose(king));
    }

    [Fact]
    public void NonHnmEvent_IgnoresTheMethod()
    {
        var sky = Camp("Kirin", MonsterWindowMethods.OpenClose, eventType: "Sky");

        Assert.Null(DiscordEventMessageBuilder.WindowMethodFor(sky));
        Assert.False(DiscordEventMessageBuilder.PostsOpenAndClose(sky));
    }

    [Fact]
    public void UnknownMethod_FallsBackToTheLegacyRules()
    {
        var king = Camp("Behemoth/King Behemoth", "Sometimes", 7, 10, 7);

        Assert.Null(DiscordEventMessageBuilder.WindowMethodFor(king));
        Assert.Equal(2, DiscordEventMessageBuilder.AttendancePostCount(king));
    }

    // THE REGRESSION GUARD. "Post count == 2" was how every call site recognised an Open/Close camp,
    // and Window N with two windows makes that wrong. Each must ask PostsOpenAndClose instead, so a
    // new call site written the old way fails here rather than mislabelling a camp.
    [Fact]
    public void NoCallSiteTestsThePostCountAgainstTwo()
    {
        var repoRoot = FindRepoRoot();
        var pattern = new Regex(@"(?<!Legacy)AttendancePostCount\([^;]*?\)\s*[!=]=\s*2");
        var offenders = new[] { "Controllers", "Services" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(repoRoot, folder), "*.cs", SearchOption.AllDirectories))
            .Where(path => pattern.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(repoRoot, path))
            .ToList();

        Assert.Empty(offenders);
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

// What the addon is told about a camp's shape (AddonEventWindowShape), for the camps it draws.
public class AddonEventWindowShapeTests
{
    private static Event Camp(string monster, string? method, int? spawnWindows, int? spawnMinutes) => new()
    {
        EventName = monster,
        EventType = "HNM",
        AssignedMonsterName = monster,
        StartTime = new DateTime(2026, 10, 28, 18, 0, 0, DateTimeKind.Utc),
        SpawnWindowCount = spawnWindows,
        SpawnWindowMinutes = spawnMinutes,
        WindowMethod = method,
    };

    [Fact]
    public void King_IsOpenClose_OnATenMinuteGrid()
    {
        var shape = AddonEventWindowShape.For(Camp("Behemoth/King Behemoth", MonsterWindowMethods.OpenClose, 7, 10));

        Assert.Equal(MonsterWindowMethods.OpenClose, shape.WindowMethod);
        Assert.True(shape.PostsOpenAndClose);
        Assert.False(shape.WindowsOpenEnded);
        Assert.Equal(600, shape.WindowCadenceSeconds);
    }

    [Fact]
    public void CustomNmWithNoGrid_UnderWindowN_IsOpenEnded_WithNoCadence()
    {
        var shape = AddonEventWindowShape.For(Camp("King Vinegarroon", MonsterWindowMethods.WindowN, null, null));

        Assert.False(shape.PostsOpenAndClose);
        Assert.True(shape.WindowsOpenEnded);
        Assert.Null(shape.WindowCadenceSeconds);
    }

    // An unstamped camp reports no method, so the addon falls back to its old post-count test.
    [Fact]
    public void UnstampedCamp_ReportsNoMethod()
    {
        var shape = AddonEventWindowShape.For(Camp("Tiamat", null, null, null));

        Assert.Null(shape.WindowMethod);
        Assert.False(shape.PostsOpenAndClose);
        Assert.Equal(3600, shape.WindowCadenceSeconds);
    }
}
