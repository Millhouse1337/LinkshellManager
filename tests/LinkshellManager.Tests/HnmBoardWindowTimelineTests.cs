using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The board's window timeline: which window the camp is on, when that window's pop chance PASSED,
// and how long until the next one. The passed line exists because the heading number alone can't say
// where in a window you are — a camp 44 minutes into window 1 rendered identically to one that had
// just flipped to it, and deeper in a camp nothing else on the board dates the current window at all
// ("Event Started" only ever describes window 1).
//
// It says PASSED rather than "opened": these monsters show within ~20 seconds of a boundary, so the
// moment a window turns over its chance is spent. "Window 6 opened" sitting above a 57-minute
// countdown read as "window 6 stays open for another 57 minutes", the opposite of what it means.
public class HnmBoardWindowTimelineTests
{
    // Mirrors DiscordBotClient.JsonOptions so assertions see the wire JSON.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly DateTime Anchor = new(2026, 8, 6, 10, 27, 56, DateTimeKind.Utc);

    private static long Unix(DateTime utc) =>
        ((DateTimeOffset)DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

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

    private static Event Camp(
        string monster,
        int window,
        DateTime? commencedAt = null,
        int cadenceMinutes = 60) => new()
    {
        Id = 1,
        EventName = monster,
        EventType = "HNM",
        AssignedMonsterName = monster,
        StartTime = Anchor,
        WindowAnchorAt = Anchor,
        CommencementStartTime = commencedAt ?? Anchor,
        HnmWindowNumber = window,
        NextWindowAt = Anchor.AddMinutes(window * (double)cadenceMinutes),
    };

    // The board's message content, unescaped. Going through the wire JSON and back out is
    // deliberate — it is the string Discord actually receives — but System.Text.Json escapes the
    // `<` of every `<t:...>` timestamp token, so read the property rather than matching raw JSON.
    private static string Content(Event ev)
    {
        var payload = DiscordEventMessageBuilder.Build(
            ev, Array.Empty<EventSignupLine>(), Setup(), new Dictionary<int, EventPartySlotSignup>());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, Wire));
        return doc.RootElement.TryGetProperty("content", out var content)
            ? content.GetString() ?? string.Empty
            : string.Empty;
    }

    // Window 1 opens AT the anchor, so a camp sitting in it dates the line to the camp's own start.
    [Fact]
    public void Window1_IsDatedToTheAnchor()
    {
        var json = Content(Camp("Tiamat", 1));
        Assert.Contains($"Window 1 passed <t:{Unix(Anchor)}:T>", json, StringComparison.Ordinal);
    }

    // Window N opens at anchor + (N-1)xcadence — the same grid HnmWindowAdvanceBackgroundService
    // counts on, so the board can't disagree with the service about when a window turned over.
    [Theory]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(9, 480)]
    public void LaterWindows_SitOnTheCadenceGrid(int window, int minutesAfterAnchor)
    {
        var json = Content(Camp("Tiamat", window));
        var expected = Unix(Anchor.AddMinutes(minutesAfterAnchor));
        Assert.Contains($"Window {window} passed <t:{expected}:T>", json, StringComparison.Ordinal);
    }

    // Past above future: the passed line and the countdown read as one timeline down the board.
    [Fact]
    public void PassedLine_RendersAboveTheCountdown()
    {
        var json = Content(Camp("Tiamat", 3));
        var passed = json.IndexOf("Window 3 passed", StringComparison.Ordinal);
        var next = json.IndexOf("Next window 4", StringComparison.Ordinal);

        Assert.True(passed >= 0, "the passed line should render");
        Assert.True(next >= 0, "the countdown should still render");
        Assert.True(passed < next, "passed must precede the countdown");
    }

    // The three window lines are one timeline, and the heading belongs at the FRONT of it: the
    // camp is on window 3 (that's what the roster is signing up for and what the countdown is
    // counting to), and window 2 is the one behind it. The heading used to sit on window 2 here —
    // reading as the camp being on a window the line below it declared passed.
    [Fact]
    public void WyrmBoard_HeadsTheAwaitedWindow_OverThePassedOne()
    {
        var json = Content(Camp("Tiamat", 2));
        Assert.Contains("Window 3 of 25", json, StringComparison.Ordinal); // heading
        Assert.Contains("Window 2 passed", json, StringComparison.Ordinal);
        Assert.Contains("Next window 3", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Window 2 of 25", json, StringComparison.Ordinal);
    }

    // A queued board hasn't reached a window boundary yet.
    [Fact]
    public void QueuedBoard_HasNoPassedLine()
    {
        var queued = Camp("Tiamat", 1);
        queued.CommencementStartTime = null;
        queued.NextWindowAt = null;

        Assert.DoesNotContain("passed <t:", Content(queued), StringComparison.Ordinal);
    }

    // No timed cadence means no grid to date a window against — say nothing rather than guess.
    [Fact]
    public void MonsterWithNoCadence_HasNoPassedLine()
    {
        var kirin = Camp("Kirin", 1);
        Assert.Equal(0, HnmConfig.WindowAdvanceMinutes("Kirin"));
        Assert.DoesNotContain("passed <t:", Content(kirin), StringComparison.Ordinal);
    }

    // An officer starting a camp EARLY puts window 1's boundary in the future, and "passed in 20
    // minutes" is nonsense. The builder reads no clock, so this is decided structurally: window 1
    // counts as passed only when the grid anchor is at or before the moment the camp went live.
    [Fact]
    public void EarlyManualStart_WithholdsTheLineUntilWindow1ActuallyOpens()
    {
        var early = Camp("Tiamat", 1, commencedAt: Anchor.AddMinutes(-20));
        Assert.DoesNotContain("passed <t:", Content(early), StringComparison.Ordinal);

        // Windows past the first need no such check — the advancer only moves the counter once a
        // boundary has genuinely passed, so reaching window 2 is itself the proof.
        var advanced = Camp("Tiamat", 2, commencedAt: Anchor.AddMinutes(-20));
        Assert.Contains("Window 2 passed", Content(advanced), StringComparison.Ordinal);
    }

    // The short band gets the line too, on its own 10-minute grid.
    [Fact]
    public void ShortWindowCamp_DatesItsWindowOnTheTenMinuteGrid()
    {
        var fafnir = Camp("Fafnir", 3, cadenceMinutes: 10);
        var expected = Unix(Anchor.AddMinutes(20));
        Assert.Contains($"Window 3 passed <t:{expected}:T>", Content(fafnir), StringComparison.Ordinal);
    }
}
