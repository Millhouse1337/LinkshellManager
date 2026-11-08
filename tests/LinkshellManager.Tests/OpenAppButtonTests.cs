using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Every event board ends with one green "Open LSManager App" button: the last button of the
// last row, on both the party board and the simple (no party setup) board.
public class OpenAppButtonTests
{
    private static readonly DateTime Start = new(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);

    private static Event Camp() => new()
    {
        Id = 7,
        EventName = "Tiamat",
        EventType = "HNM",
        AssignedMonsterName = "Tiamat",
        StartTime = Start,
        CreationSource = "App",
    };

    private static PartySetup Setup() => new()
    {
        Id = 31,
        Name = "HNM",
        Alliances = new List<PartySetupAlliance>
        {
            new()
            {
                Id = 21, PartySetupId = 31, SortOrder = 0, Name = "Alliance 1",
                Parties = new List<PartySetupParty>
                {
                    new()
                    {
                        Id = 11, PartySetupAllianceId = 21, SortOrder = 0, Name = "Party 1",
                        Slots = new List<PartySetupSlot>
                        {
                            new() { Id = 101, PartySetupPartyId = 11, SortOrder = 0, RequirementType = "Any" },
                        },
                    },
                },
            },
        },
    };

    private static List<JsonElement> Rows(object payload)
    {
        var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return doc.RootElement.GetProperty("components").EnumerateArray().ToList();
    }

    private static void AssertOpenAppIsLast(List<JsonElement> rows)
    {
        var lastButton = rows[^1].GetProperty("components").EnumerateArray().Last();
        Assert.Equal("Open LSManager App", lastButton.GetProperty("label").GetString());
        Assert.Equal(3, lastButton.GetProperty("style").GetInt32()); // green
        Assert.Equal(DiscordEventMessageBuilder.LaunchAppButtonId, lastButton.GetProperty("custom_id").GetString());

        // Exactly one on the board.
        var count = rows.SelectMany(r => r.GetProperty("components").EnumerateArray())
            .Count(b => b.GetProperty("custom_id").GetString() == DiscordEventMessageBuilder.LaunchAppButtonId);
        Assert.Equal(1, count);
    }

    [Fact]
    public void PartyBoard_EndsWithOpenAppButton()
    {
        var rows = Rows(DiscordEventMessageBuilder.Build(
            Camp(), Array.Empty<EventSignupLine>(), Setup(), new Dictionary<int, EventPartySlotSignup>()));

        AssertOpenAppIsLast(rows);
    }

    [Fact]
    public void SimpleBoard_EndsWithOpenAppButton()
    {
        var rows = Rows(DiscordEventMessageBuilder.Build(Camp(), Array.Empty<EventSignupLine>()));

        AssertOpenAppIsLast(rows);
    }
}
