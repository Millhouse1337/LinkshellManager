using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LinkshellManagerDiscordApp.Models;
using Xunit;
using Rules = LinkshellManagerDiscordApp.Services.HnmCampRules;

namespace LinkshellManager.Tests;

// Pins the answer of every HNM camp rule (window counts, the focus window, post counts, Window
// Method, labels...) for a large grid of made-up camps.
//
// Why: these rules decide which window a post is filed against and how many posts a camp takes,
// which is what DKP is paid on. They are being moved out of the Discord message builder into
// their own class, and that move must not change a single answer. The `Rules` alias at the top is
// the only line that should change when the rules move.
//
// The snapshot holds two things:
//   - one readable line per camp for a few hundred hand-picked camps, so a change shows WHICH camp
//     answers differently and how;
//   - a digest (SHA-256) of the same lines for the full grid (tens of thousands of camps), which
//     is too big to keep as text but catches a change in any corner of it.
//
// When a change to the rules is MEANT, regenerate the file:
//   PowerShell:  $env:UPDATE_HNM_RULES_SNAPSHOT=1; dotnet test tests/LinkshellManager.Tests --filter HnmCampRulesSnapshotTests; Remove-Item Env:UPDATE_HNM_RULES_SNAPSHOT
//   bash:        UPDATE_HNM_RULES_SNAPSHOT=1 dotnet test tests/LinkshellManager.Tests --filter HnmCampRulesSnapshotTests
// and review the diff of Snapshots/hnm-camp-rules.txt in the commit.
public class HnmCampRulesSnapshotTests
{
    private const string SnapshotFile = "hnm-camp-rules.txt";
    private const string UpdateVariable = "UPDATE_HNM_RULES_SNAPSHOT";
    private const string DigestPrefix = "full-grid ";

    private static readonly DateTime Anchor = new(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);

    // ---- The grid's dimensions ----

    private static readonly string?[] EventTypes = { "HNM", " hnm ", "Sky", null };

    private static readonly string?[] AttendanceModes = { null, HnmAttendanceModes.Standard, HnmAttendanceModes.Wd };

    // (AssignedMonsterName, EventName). The last two have no monster, so the rules fall back to
    // the event's name.
    private static readonly (string? Monster, string? Name)[] Monsters =
    {
        ("Tiamat", "Tiamat"),                    // wyrm: 25 x 60 min, wipes its roster
        ("Cerberus", "Cerberus"),                // ToAU: 5 x 6 h
        ("Fafnir/Nidhogg", "Fafnir/Nidhogg"),    // merged NQ/HQ pair: 7 x 10 min
        ("King Behemoth", "King Behemoth"),      // HQ half: 7 x 10 min
        ("Goblin Furrier", "Goblin Furrier"),    // testing preset: 2 posts, no cadence
        ("Kirin", "Kirin"),                      // no cadence at all
        (null, "Behemoth/King Behemoth"),        // name-only lookup
        (null, "Party"),                         // plain event
    };

    private static readonly string?[] WindowMethods = { null, MonsterWindowMethods.OpenClose, MonsterWindowMethods.WindowN, "bogus" };

    private static readonly int?[] WindowCountOverrides = { null, 1, 2, 7 };

    // (SpawnWindowCount, SpawnWindowMinutes) stamped from the linkshell's monster setup.
    private static readonly (int? Count, int? Minutes)[] SpawnGrids = { (null, null), (1, 0), (5, 30), (40, 15) };

    // The camp's progress: which window it is on, and whether it has a next window / ended.
    private sealed record WindowState(string Label, int Number, bool HasNextAt, bool Finalized, bool Defeated);

    private static readonly WindowState[] WindowStates =
    {
        new("w0", 0, false, false, false),
        new("w1", 1, true, false, false),
        new("w3", 3, true, false, false),
        new("w99", 99, true, false, false),
        new("w3-finalized", 3, true, true, false),
        new("w3-defeated", 3, true, false, true),
        new("w3-nonext", 3, false, false, false),
    };

    private sealed record Camp(
        string? EventType, string? AttendanceMode, (string? Monster, string? Name) Monster,
        string? WindowMethod, int? WindowCountOverride, (int? Count, int? Minutes) Spawn, WindowState State);

    private static Event ToEvent(Camp camp) => new()
    {
        Id = 1,
        EventType = camp.EventType,
        AttendanceMode = camp.AttendanceMode,
        AssignedMonsterName = camp.Monster.Monster,
        EventName = camp.Monster.Name,
        WindowMethod = camp.WindowMethod,
        WindowCountOverride = camp.WindowCountOverride,
        SpawnWindowCount = camp.Spawn.Count,
        SpawnWindowMinutes = camp.Spawn.Minutes,
        HnmWindowNumber = camp.State.Number,
        StartTime = Anchor,
        CommencementStartTime = Anchor,
        NextWindowAt = camp.State.HasNextAt ? Anchor.AddHours(1) : null,
        WdFinalizedAt = camp.State.Finalized ? Anchor.AddHours(2) : null,
        HnmDefeatedAt = camp.State.Defeated ? Anchor.AddHours(2) : null,
    };

    private static string Describe(Camp camp) =>
        $"type={Show(camp.EventType)} mode={Show(camp.AttendanceMode)} monster={Show(camp.Monster.Monster)} name={Show(camp.Monster.Name)} "
        + $"method={Show(camp.WindowMethod)} override={Show(camp.WindowCountOverride)} "
        + $"spawn={Show(camp.Spawn.Count)}x{Show(camp.Spawn.Minutes)} state={camp.State.Label}";

    // Every rule's answer for one camp, on one line.
    private static string Answers(Event ev)
    {
        var labels = string.Join(",", new[] { 1, 2, 3, 4 }.Select(seq => Show(Rules.DefaultWindowLabel(ev, seq))));
        return string.Join(" ", new[]
        {
            $"IsWd={Show(Rules.IsWd(ev))}",
            $"IsHnm={Show(Rules.IsHnm(ev))}",
            $"UsesWindows={Show(Rules.UsesWindows(ev))}",
            $"Focus={Rules.FocusWindow(ev)}",
            $"Opened={Rules.OpenedWindow(ev)}",
            $"SpawnNow={Show(Rules.SpawnWindowNow(ev))}",
            $"HasNext={Show(Rules.HasNextWindow(ev))}",
            $"Wipes={Show(Rules.ClearsRosterOnWindowAdvance(ev))}",
            $"Count={Rules.EffectiveWindowCount(ev)}",
            $"Minutes={Rules.EffectiveWindowMinutes(ev)}",
            $"Posts={Rules.AttendancePostCount(ev)}",
            $"LegacyPosts={Rules.LegacyAttendancePostCount(ev)}",
            $"Method={Show(Rules.WindowMethodFor(ev))}",
            $"OpenClose={Show(Rules.PostsOpenAndClose(ev))}",
            $"OpenEnded={Show(Rules.WindowsOpenEnded(ev))}",
            $"MaxSeq={Rules.MaxPostSequence(ev)}",
            $"KillCeiling={Rules.KillPostCeiling(ev)}",
            $"Labels=[{labels}]",
        });
    }

    private static string Line(Camp camp) => $"{Describe(camp)} => {Answers(ToEvent(camp))}";

    private static string Show(string? value) => value is null ? "null" : $"'{value}'";

    private static string Show(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static string Show(bool value) => value ? "T" : "F";

    // ---- The full grid and the hand-picked rows ----

    private static IEnumerable<Camp> FullGrid() =>
        from type in EventTypes
        from mode in AttendanceModes
        from monster in Monsters
        from method in WindowMethods
        from windowOverride in WindowCountOverrides
        from spawn in SpawnGrids
        from state in WindowStates
        select new Camp(type, mode, monster, method, windowOverride, spawn, state);

    // A few hundred rows, each varying one or two things from a mid-camp HNM, so a failing row
    // says plainly which input mattered.
    private static IEnumerable<Camp> ReadableRows()
    {
        var midCamp = WindowStates.Single(state => state.Label == "w3");
        var noGrid = SpawnGrids[0];

        // Every monster x Window Method, in both attendance modes.
        foreach (var mode in new[] { HnmAttendanceModes.Standard, HnmAttendanceModes.Wd })
        foreach (var monster in Monsters)
        foreach (var method in WindowMethods)
        {
            yield return new Camp("HNM", mode, monster, method, null, noGrid, midCamp);
        }

        // The window count's inputs fighting each other: override x stamped grid x method.
        foreach (var monster in new[] { Monsters[0], Monsters[3], Monsters[5] })
        foreach (var windowOverride in WindowCountOverrides)
        foreach (var spawn in SpawnGrids)
        foreach (var method in WindowMethods)
        {
            yield return new Camp("HNM", HnmAttendanceModes.Standard, monster, method, windowOverride, spawn, midCamp);
        }

        // Progress through the camp, on a wyrm, a king and a no-cadence monster.
        foreach (var mode in new[] { HnmAttendanceModes.Standard, HnmAttendanceModes.Wd })
        foreach (var monster in new[] { Monsters[0], Monsters[3], Monsters[5] })
        foreach (var state in WindowStates)
        {
            yield return new Camp("HNM", mode, monster, null, null, noGrid, state);
        }

        // The event type spelling, which decides IsHnm and so the Window Method.
        foreach (var type in EventTypes)
        foreach (var monster in new[] { Monsters[0], Monsters[3], Monsters[7] })
        {
            yield return new Camp(type, null, monster, MonsterWindowMethods.WindowN, 2, noGrid, midCamp);
        }
    }

    internal static List<string> BuildSnapshot()
    {
        var lines = ReadableRows().Select(Line).ToList();
        var full = FullGrid().Select(Line).ToList();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", full))));
        lines.Add($"{DigestPrefix}camps={full.Count} sha256={digest}");
        return lines;
    }

    [Fact]
    public void EveryRuleAnswerMatchesTheSnapshot()
    {
        var actual = BuildSnapshot();
        var path = Path.Combine(SnapshotFolder.Find(), SnapshotFile);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, string.Join("\n", actual) + "\n");
            return;
        }

        Assert.True(File.Exists(path), $"No HNM rules snapshot at {path}. Run the test once with {UpdateVariable}=1 to write it.");
        var expected = File.ReadAllText(path)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToList();

        if (expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            return;
        }

        var removed = expected.Except(actual, StringComparer.Ordinal).Select(line => "- " + line).Take(40);
        var added = actual.Except(expected, StringComparer.Ordinal).Select(line => "+ " + line).Take(40);
        Assert.Fail(
            "The HNM camp rules no longer give the answers in tests/LinkshellManager.Tests/Snapshots/" + SnapshotFile + ".\n"
            + "(- = expected, + = what the rules answer now; a changed 'full-grid' line alone means a camp outside the readable rows changed)\n"
            + string.Join("\n", removed.Concat(added)) + "\n\n"
            + $"If this change is meant, regenerate the file: set {UpdateVariable}=1, run this test again, "
            + "and commit the new snapshot with the change.");
    }

    // So a broken grid can't pass by matching a tiny snapshot.
    [Fact]
    public void TheGridCoversEveryCombination()
    {
        var expectedCount = EventTypes.Length * AttendanceModes.Length * Monsters.Length * WindowMethods.Length
            * WindowCountOverrides.Length * SpawnGrids.Length * WindowStates.Length;
        Assert.Equal(expectedCount, FullGrid().Count());
        Assert.True(ReadableRows().Count() > 200);
    }

    // A few answers written out by hand, so the snapshot isn't the only thing saying what is right.
    [Fact]
    public void HandCheckedAnswers()
    {
        var wyrm = ToEvent(new Camp("HNM", null, Monsters[0], null, null, SpawnGrids[0], WindowStates[2]));
        Assert.Equal(25, Rules.EffectiveWindowCount(wyrm));
        Assert.Equal(60, Rules.EffectiveWindowMinutes(wyrm));
        Assert.Equal(3, Rules.OpenedWindow(wyrm));
        Assert.Equal(4, Rules.FocusWindow(wyrm));
        Assert.True(Rules.ClearsRosterOnWindowAdvance(wyrm));

        var king = ToEvent(new Camp("HNM", null, Monsters[3], null, null, SpawnGrids[0], WindowStates[2]));
        Assert.Equal(7, Rules.EffectiveWindowCount(king));
        Assert.Equal(2, Rules.AttendancePostCount(king));
        Assert.True(Rules.PostsOpenAndClose(king));
        Assert.Equal("Close", Rules.DefaultWindowLabel(king, 2));
        Assert.Equal("Kill", Rules.DefaultWindowLabel(king, 3));

        var openEnded = ToEvent(new Camp("HNM", null, Monsters[5], MonsterWindowMethods.WindowN, null, SpawnGrids[0], WindowStates[1]));
        Assert.True(Rules.WindowsOpenEnded(openEnded));
        Assert.Equal(25, Rules.AttendancePostCount(openEnded));
        Assert.Equal(26, Rules.KillPostCeiling(openEnded));
        Assert.Null(Rules.SpawnWindowNow(openEnded));
    }
}

// Finds tests/LinkshellManager.Tests/Snapshots from the test's bin folder.
internal static class SnapshotFolder
{
    public static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var project = Path.Combine(directory.FullName, "tests", "LinkshellManager.Tests");
            if (Directory.Exists(project))
            {
                var snapshots = Path.Combine(project, "Snapshots");
                Directory.CreateDirectory(snapshots);
                return snapshots;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate tests/LinkshellManager.Tests above {AppContext.BaseDirectory}.");
    }
}
