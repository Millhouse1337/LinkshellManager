using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the exact Discord message (content, embed, buttons) the board builder makes for a handful
// of typical events: a plain roster event, a wyrm camp mid-band, a king on its Open/Close posts, a
// Manual Check In camp, a defeated camp across two alliances, a custom NM with its own window grid,
// and a finished Manual Check In wyrm.
//
// Why: the HNM rules the board reads (window counts, which window to head, whether the roster
// wipes) are being moved out of the builder into their own class. Every board already posted in
// Discord is re-rendered by this code, so the move must not change a single character of it.
//
// When a change to the boards is MEANT, regenerate the file:
//   PowerShell:  $env:UPDATE_BOARD_SNAPSHOT=1; dotnet test tests/LinkshellManager.Tests --filter DiscordBoardRenderSnapshotTests; Remove-Item Env:UPDATE_BOARD_SNAPSHOT
//   bash:        UPDATE_BOARD_SNAPSHOT=1 dotnet test tests/LinkshellManager.Tests --filter DiscordBoardRenderSnapshotTests
// and review the diff of Snapshots/discord-boards.txt in the commit.
public class DiscordBoardRenderSnapshotTests
{
    private const string SnapshotFile = "discord-boards.txt";
    private const string UpdateVariable = "UPDATE_BOARD_SNAPSHOT";

    // The wire options (DiscordBotClient.JsonOptions), indented and with readable characters so a
    // diff of the snapshot can be read. Only how the text is written differs, not what is in it.
    private static readonly JsonSerializerOptions Readable = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly DateTime Anchor = new(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);

    private sealed record Board(
        string Name,
        Event Event,
        IReadOnlyList<EventSignupLine> Signups,
        PartySetup? Setup = null,
        IReadOnlyDictionary<int, EventPartySlotSignup>? SlotSignups = null);

    private static PartySetup Setup(int alliances)
    {
        var slotId = 100;
        return new PartySetup
        {
            Id = 31,
            Name = "HNM",
            Alliances = Enumerable.Range(0, alliances).Select(a => new PartySetupAlliance
            {
                Id = 20 + a,
                PartySetupId = 31,
                SortOrder = a,
                Name = $"Alliance {a + 1}",
                Parties = Enumerable.Range(0, 2).Select(p => new PartySetupParty
                {
                    Id = 10 * (a + 1) + p,
                    PartySetupAllianceId = 20 + a,
                    SortOrder = p,
                    Name = $"Party {p + 1}",
                    Slots = new List<PartySetupSlot>
                    {
                        new() { Id = ++slotId, SortOrder = 0, RequirementType = "Role", Role = "Tank", IsPartyLeader = true },
                        new() { Id = ++slotId, SortOrder = 1, RequirementType = "Role", Role = "Healer" },
                        new() { Id = ++slotId, SortOrder = 2, RequirementType = "Job", MainJob = "BRD", SubJob = "WHM" },
                        new() { Id = ++slotId, SortOrder = 3, RequirementType = "Any" },
                    },
                }).ToList(),
            }).ToList(),
        };
    }

    private static Dictionary<int, EventPartySlotSignup> Seated(params (int SlotId, string Name, string Main, string? Sub, bool Stay)[] seats) =>
        seats.ToDictionary(
            seat => seat.SlotId,
            seat => new EventPartySlotSignup
            {
                Id = seat.SlotId + 1000,
                EventId = 1,
                PartySetupSlotId = seat.SlotId,
                CharacterName = seat.Name,
                MainJob = seat.Main,
                SubJob = seat.Sub,
                StayNextWindow = seat.Stay,
                IsPartyLeader = seat.SlotId == 101,
                SignedUpAtUtc = Anchor,
            });

    private static Event Hnm(string monster, Action<Event>? tweak = null)
    {
        var ev = new Event
        {
            Id = 1,
            EventName = monster,
            EventType = "HNM",
            AssignedMonsterName = monster,
            EventLocation = "Dragon's Aery",
            StartTime = Anchor,
            WindowAnchorAt = Anchor,
            CommencementStartTime = Anchor,
            HnmWindowNumber = 1,
        };
        tweak?.Invoke(ev);
        return ev;
    }

    private static IEnumerable<Board> Boards()
    {
        yield return new Board(
            "plain roster event (no party setup)",
            new Event
            {
                Id = 1,
                EventName = "Sky Farm",
                EventType = "Sky",
                EventLocation = "Ru'Aun Gardens",
                DkpPerHour = 4,
                Details = "Bring sleep resist.",
                StartTime = Anchor,
            },
            new[]
            {
                new EventSignupLine("Mill", "WAR", "NIN", "Tank"),
                new EventSignupLine("Ash", "WHM", "BLM", "Healer", EnfeebReady: true),
            });

        yield return new Board(
            "wyrm mid-band, one seat locked to stay",
            Hnm("Tiamat", ev =>
            {
                ev.EventLocation = "Attohwa Chasm";
                ev.HnmWindowNumber = 3;
                ev.NextWindowAt = Anchor.AddHours(3);
                ev.DayNumber = 2;
            }),
            new[] { new EventSignupLine("Late", "RDM", "WHM", "Support") },
            Setup(1),
            Seated((101, "Mill", "PLD", "WAR", true), (102, "Ash", "WHM", null, false)));

        yield return new Board(
            "king queued on Open/Close posts",
            Hnm("Behemoth/King Behemoth", ev =>
            {
                ev.EventLocation = "Behemoth's Dominion";
                ev.CommencementStartTime = null;
                ev.DayNumber = 5;
                ev.WindowMethod = MonsterWindowMethods.OpenClose;
            }),
            Array.Empty<EventSignupLine>(),
            Setup(1),
            new Dictionary<int, EventPartySlotSignup>());

        yield return new Board(
            "Manual Check In king with x'd-in members",
            Hnm("Fafnir/Nidhogg", ev =>
            {
                ev.AttendanceMode = HnmAttendanceModes.Wd;
                ev.HnmWindowNumber = 2;
                ev.NextWindowAt = Anchor.AddMinutes(20);
                ev.DayNumber = 4;
            }),
            new[]
            {
                new EventSignupLine("Mill", "DRG", "WAR", "DPS", WdArrivalWindow: 1),
                new EventSignupLine("Ash", "BRD", "WHM", "Support", WdArrivalWindow: 2, ResistReady: true),
            },
            Setup(1),
            Seated((103, "Mill", "BRD", "WHM", false)));

        yield return new Board(
            "defeated camp across two alliances",
            Hnm("Adamantoise", ev =>
            {
                ev.EventLocation = "Valley of Sorrows";
                ev.HnmWindowNumber = 4;
                ev.NextWindowAt = Anchor.AddMinutes(40);
                ev.HnmDefeatedAt = Anchor.AddMinutes(35);
            }),
            Array.Empty<EventSignupLine>(),
            Setup(2),
            Seated((101, "Mill", "PLD", "WAR", false), (109, "Ash", "WHM", "BLM", false)));

        yield return new Board(
            "custom NM with its own window grid, Every Window",
            Hnm("Serket", ev =>
            {
                ev.EventLocation = "Garlaige Citadel";
                ev.SpawnWindowCount = 5;
                ev.SpawnWindowMinutes = 30;
                ev.WindowMethod = MonsterWindowMethods.WindowN;
                ev.HnmWindowNumber = 2;
                ev.NextWindowAt = Anchor.AddMinutes(60);
            }),
            Array.Empty<EventSignupLine>(),
            Setup(1),
            new Dictionary<int, EventPartySlotSignup>());

        yield return new Board(
            "finished Manual Check In wyrm",
            Hnm("Jormungand", ev =>
            {
                ev.EventLocation = "Uleguerand Range";
                ev.AttendanceMode = HnmAttendanceModes.Wd;
                ev.HnmWindowNumber = 25;
                ev.WdFinalizedAt = Anchor.AddHours(25);
                ev.WindowCountOverride = 25;
            }),
            new[] { new EventSignupLine("Mill", "SAM", "WAR", "DPS", WdArrivalWindow: 7) },
            Setup(1),
            new Dictionary<int, EventPartySlotSignup>());

        yield return new Board(
            "timed HNM camp with no party setup",
            Hnm("Cerberus", ev =>
            {
                ev.EventLocation = "Mount Zhayolm";
                ev.HnmWindowNumber = 2;
                ev.NextWindowAt = Anchor.AddHours(12);
                ev.DkpPerHour = 9;
            }),
            new[] { new EventSignupLine("Mill", "NIN", "WAR", "Tank") });
    }

    internal static List<string> BuildSnapshot()
    {
        var lines = new List<string>();
        foreach (var board in Boards())
        {
            var payload = DiscordEventMessageBuilder.Build(board.Event, board.Signups, board.Setup, board.SlotSignups);
            lines.Add($"=== {board.Name}");
            lines.AddRange(JsonSerializer.Serialize(payload, Readable).Split('\n').Select(line => line.TrimEnd('\r')));
        }
        return lines;
    }

    [Fact]
    public void EveryBoardMatchesTheSnapshot()
    {
        var actual = BuildSnapshot();
        var path = Path.Combine(SnapshotFolder.Find(), SnapshotFile);

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, string.Join("\n", actual) + "\n");
            return;
        }

        Assert.True(File.Exists(path), $"No board snapshot at {path}. Run the test once with {UpdateVariable}=1 to write it.");
        var expected = File.ReadAllText(path)
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToList();
        var actualLines = actual.Where(line => line.Length > 0).ToList();

        if (expected.SequenceEqual(actualLines, StringComparer.Ordinal))
        {
            return;
        }

        var firstDifference = Enumerable.Range(0, Math.Max(expected.Count, actualLines.Count))
            .First(i => i >= expected.Count || i >= actualLines.Count || !string.Equals(expected[i], actualLines[i], StringComparison.Ordinal));
        var board = actualLines.Take(firstDifference + 1).LastOrDefault(line => line.StartsWith("=== ", StringComparison.Ordinal));
        Assert.Fail(
            "A Discord board no longer renders as in tests/LinkshellManager.Tests/Snapshots/" + SnapshotFile + ".\n"
            + $"First difference at line {firstDifference + 1} (board: {board}):\n"
            + $"- {(firstDifference < expected.Count ? expected[firstDifference] : "(end of file)")}\n"
            + $"+ {(firstDifference < actualLines.Count ? actualLines[firstDifference] : "(end of output)")}\n\n"
            + $"If this change is meant, regenerate the file: set {UpdateVariable}=1, run this test again, "
            + "and commit the new snapshot with the change.");
    }

    // So an empty render can't pass by matching an empty snapshot.
    [Fact]
    public void TheBoardsCarryTheirWindowHeadings()
    {
        var text = string.Join("\n", BuildSnapshot());
        Assert.Contains("Tiamat · Window 4 of 25", text, StringComparison.Ordinal);
        Assert.Contains("Window 3 passed", text, StringComparison.Ordinal);
        Assert.Contains("Serket · Window 3 of 5", text, StringComparison.Ordinal);
        Assert.Equal(Boards().Count(), BuildSnapshot().Count(line => line.StartsWith("=== ", StringComparison.Ordinal)));
    }
}
