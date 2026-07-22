using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the Components V2 event-board payloads. The whole point of the V2 mode is to
// escape the embed's width, so the invariants that MUST hold are: the IS_COMPONENTS_V2
// flag (32768) is set, `content`/`embeds` are absent (Discord rejects them on a V2
// message), the board PNG is referenced from a media gallery (type 12) via attachment://,
// the same signup buttons survive, and the wide canvas is actually requested.
public class ComponentsV2BoardTests
{
    // Serializer mirrors DiscordBotClient.JsonOptions (camelCase + drop nulls) so the
    // assertions see the exact JSON that would go on the wire.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // A two-alliance board with a couple of seated members and one open slot, so both the
    // wide layout and the multi-section roster text fallback get exercised.
    private static (Event Ev, PartySetup Setup, Dictionary<int, EventPartySlotSignup> Signups) BuildBoard()
    {
        var a1p1 = new PartySetupParty
        {
            Id = 11, PartySetupAllianceId = 21, SortOrder = 0, Name = "Party 1",
            Slots = new List<PartySetupSlot>
            {
                new() { Id = 101, PartySetupPartyId = 11, SortOrder = 0, RequirementType = "Any" },
                new() { Id = 102, PartySetupPartyId = 11, SortOrder = 1, RequirementType = "Any" },
            },
        };
        var a2p1 = new PartySetupParty
        {
            Id = 12, PartySetupAllianceId = 22, SortOrder = 0, Name = "Party 1",
            Slots = new List<PartySetupSlot>
            {
                new() { Id = 201, PartySetupPartyId = 12, SortOrder = 0, RequirementType = "Any" },
            },
        };
        var setup = new PartySetup
        {
            Id = 31, Name = "Sky",
            Alliances = new List<PartySetupAlliance>
            {
                new() { Id = 21, PartySetupId = 31, SortOrder = 0, Name = "Alliance 1", Parties = new List<PartySetupParty> { a1p1 } },
                new() { Id = 22, PartySetupId = 31, SortOrder = 1, Name = "Alliance 2", Parties = new List<PartySetupParty> { a2p1 } },
            },
        };
        var ev = new Event { Id = 7, EventName = "Kirin", EventType = "Sky" };
        var signups = new Dictionary<int, EventPartySlotSignup>
        {
            [101] = new() { Id = 1, EventId = 7, PartySetupSlotId = 101, CharacterName = "Alpha", Role = "Tank", MainJob = "PLD", SubJob = "NIN", IsPartyLeader = true },
            [201] = new() { Id = 2, EventId = 7, PartySetupSlotId = 201, CharacterName = "Bravo", Role = "DPS", MainJob = "WAR", SubJob = "NIN" },
            // slot 102 intentionally left open
        };
        return (ev, setup, signups);
    }

    [Fact]
    public void ImageV2_SetsFlag_UsesMediaGallery_DropsContentAndEmbeds()
    {
        var (ev, setup, signups) = BuildBoard();

        var payload = DiscordEventMessageBuilder.BuildBoardImageV2Message(
            ev, Array.Empty<EventSignupLine>(), setup, signups, "event-7-board.png");
        var json = JsonSerializer.Serialize(payload, Wire);

        Assert.Contains("\"flags\":32768", json);            // IS_COMPONENTS_V2
        Assert.Contains("\"type\":17", json);                // container
        Assert.Contains("\"type\":12", json);                // media gallery
        Assert.Contains("attachment://event-7-board.png", json);
        Assert.Contains("\"accent_color\":", json);          // snake_case survives camelCase policy
        Assert.Contains("\"custom_id\":\"evt:pssignup:7\"", json); // signup buttons preserved
        // V2 forbids these TOP-LEVEL fields (text displays legitimately use `content`
        // nested, so check the root object, not the raw string).
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("content", out _));
        Assert.False(doc.RootElement.TryGetProperty("embeds", out _));
    }

    [Fact]
    public void FallbackV2_IsV2Flagged_NoAttachment_RostersMembers()
    {
        var (ev, setup, signups) = BuildBoard();

        var payload = DiscordEventMessageBuilder.BuildBoardV2FallbackMessage(
            ev, Array.Empty<EventSignupLine>(), setup, signups);
        var json = JsonSerializer.Serialize(payload, Wire);

        Assert.Contains("\"flags\":32768", json);   // still V2 (flag can't be toggled on edit)
        Assert.Contains("\"type\":17", json);        // container
        Assert.DoesNotContain("\"type\":12", json);  // no media gallery (render failed)
        Assert.DoesNotContain("attachment://", json);
        Assert.Contains("Alpha", json);              // roster rendered as text
        Assert.Contains("Bravo", json);
        Assert.DoesNotContain("\"embeds\":", json);
    }

    [Fact]
    public void DefeatedNoticeV2_IsV2Flagged_NoEmbeds()
    {
        var payload = DiscordEventMessageBuilder.BuildV2DefeatedNoticeMessage(
            "💀 Kirin defeated", "Will repop soon.");
        var json = JsonSerializer.Serialize(payload, Wire);

        Assert.Contains("\"flags\":32768", json);
        Assert.Contains("\"type\":17", json);
        Assert.Contains("Kirin defeated", json);
        Assert.DoesNotContain("\"embeds\":", json);
    }

    [Fact]
    public void WideHtml_UsesWiderCanvas_NarrowDoesNot()
    {
        var (ev, setup, signups) = BuildBoard();

        var wide = EventBoardHtmlBuilder.Build(ev, setup, signups, Array.Empty<EventSignupLine>(), wide: true);
        var narrow = EventBoardHtmlBuilder.Build(ev, setup, signups, Array.Empty<EventSignupLine>(), wide: false);

        Assert.Contains($".embed{{width:{EventBoardImageRenderer.WideCardWidth}px;}}", wide);
        Assert.Contains($".embed{{width:{EventBoardImageRenderer.DefaultCardWidth}px;}}", narrow);
        // The wide canvas stays within Discord's 4096px/side cap at the 2× render density.
        Assert.True(EventBoardImageRenderer.WideCardWidth * 2 <= 4096);
    }
}
