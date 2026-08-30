using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The classic embed board (the fallback when the PNG renderer is unavailable, and the shape
// used for ad-hoc boards) prints a member's readiness tags inline after their jobs. Its
// "Color Key" field therefore has to name them, exactly like the wide grid key and the image
// board's key do — a bare ⬛ next to somebody's name is otherwise a mystery.
public class EmbedBoardColorKeyTests
{
    // Mirrors DiscordBotClient.JsonOptions so assertions see the on-the-wire JSON.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static (Event Ev, PartySetup Setup, Dictionary<int, EventPartySlotSignup> Signups) BuildBoard()
    {
        var party = new PartySetupParty
        {
            Id = 11, PartySetupAllianceId = 21, SortOrder = 0, Name = "Party 1",
            Slots = new List<PartySetupSlot>
            {
                new() { Id = 101, PartySetupPartyId = 11, SortOrder = 0, RequirementType = "Any" },
                new() { Id = 102, PartySetupPartyId = 11, SortOrder = 1, RequirementType = "Any" },
            },
        };
        var setup = new PartySetup
        {
            Id = 31, Name = "Sky",
            Alliances = new List<PartySetupAlliance>
            {
                new() { Id = 21, PartySetupId = 31, SortOrder = 0, Name = "Alliance 1", Parties = new List<PartySetupParty> { party } },
            },
        };
        var ev = new Event { Id = 7, EventName = "Kirin", EventType = "Sky" };
        var signups = new Dictionary<int, EventPartySlotSignup>
        {
            [101] = new()
            {
                Id = 1, EventId = 7, PartySetupSlotId = 101, CharacterName = "Alpha",
                Role = "Tank", MainJob = "PLD", SubJob = "NIN",
                EnfeebReady = true, RelicWeapon = true,
            },
            // slot 102 intentionally left open
        };
        return (ev, setup, signups);
    }

    private static string ColorKeyValue(object payload)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Wire));
        foreach (var field in doc.RootElement.GetProperty("embeds")[0].GetProperty("fields").EnumerateArray())
        {
            if (field.GetProperty("name").GetString() == "Color Key")
            {
                return field.GetProperty("value").GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    [Fact]
    public void ColorKey_NamesEveryReadinessTag()
    {
        var (ev, setup, signups) = BuildBoard();

        var key = ColorKeyValue(DiscordEventMessageBuilder.Build(ev, Array.Empty<EventSignupLine>(), setup, signups));

        Assert.Contains("🔵 Tank", key);
        foreach (var tag in EventReadiness.All)
        {
            Assert.Contains($"{tag.Emoji} {tag.Label}", key);
        }
    }

    // The tags describe a button every board carries, so the key lists them whether or not
    // anybody on THIS board has ticked one — same rule the image board's key follows.
    [Fact]
    public void ColorKey_NamesReadinessTags_EvenWithNoneSet()
    {
        var (ev, setup, signups) = BuildBoard();
        signups[101].EnfeebReady = false;
        signups[101].RelicWeapon = false;

        var key = ColorKeyValue(DiscordEventMessageBuilder.Build(ev, Array.Empty<EventSignupLine>(), setup, signups));

        foreach (var tag in EventReadiness.All)
        {
            Assert.Contains($"{tag.Emoji} {tag.Label}", key);
        }
    }
}
