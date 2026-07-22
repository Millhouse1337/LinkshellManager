using System;
using System.Collections.Generic;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the "Stay Next Window" visibility on the rendered PNG board: a locked signup
// must show the 🔒 marker + a "Staying" tile so officers see who carries over into the
// next window (Azurth's request), and neither must appear when nothing is locked.
public class EventBoardLockRenderingTests
{
    private const string LockGlyph = "&#128274;"; // 🔒 as rendered into the board HTML

    // A one-party Tiamat (window-cycle HNM) board with two seated members.
    private static (Event Ev, PartySetup Setup, Dictionary<int, EventPartySlotSignup> Signups) BuildTiamatBoard(
        bool firstMemberLocked)
    {
        var slot1 = new PartySetupSlot { Id = 101, PartySetupPartyId = 11, SortOrder = 0, RequirementType = "Any" };
        var slot2 = new PartySetupSlot { Id = 102, PartySetupPartyId = 11, SortOrder = 1, RequirementType = "Any" };
        var party = new PartySetupParty
        {
            Id = 11, PartySetupAllianceId = 21, SortOrder = 0, Name = "Party 1",
            Slots = new List<PartySetupSlot> { slot1, slot2 },
        };
        var alliance = new PartySetupAlliance
        {
            Id = 21, PartySetupId = 31, SortOrder = 0, Name = "Alliance 1",
            Parties = new List<PartySetupParty> { party },
        };
        var setup = new PartySetup
        {
            Id = 31, Name = "Tiamat",
            Alliances = new List<PartySetupAlliance> { alliance },
        };

        var ev = new Event
        {
            Id = 1,
            EventName = "Tiamat",
            EventType = "HNM",
            AssignedMonsterName = "Tiamat", // HnmConfig.SupportsWindowAdvance == true
            HnmWindowNumber = 1,
        };

        var signups = new Dictionary<int, EventPartySlotSignup>
        {
            [101] = new EventPartySlotSignup
            {
                Id = 1, EventId = 1, PartySetupSlotId = 101, CharacterName = "Lockedguy",
                Role = "Tank", MainJob = "PLD", SubJob = "NIN", StayNextWindow = firstMemberLocked,
            },
            [102] = new EventPartySlotSignup
            {
                Id = 2, EventId = 1, PartySetupSlotId = 102, CharacterName = "Freeguy",
                Role = "DPS", MainJob = "WAR", SubJob = "NIN", StayNextWindow = false,
            },
        };
        return (ev, setup, signups);
    }

    // The 🔒 sits immediately before the member's name inside the .who span, so this
    // proves the per-slot marker (distinct from the always-present legend key).
    private const string LockedName = LockGlyph + "</span>Lockedguy";
    private const string UnlockedName = LockGlyph + "</span>Freeguy";
    private const string StayingTileLabel = "<div class=\"lab\">Staying</div>";

    [Fact]
    public void Build_WindowHnm_WithLockedSignup_ShowsLockMarkerAndStayingTile()
    {
        var (ev, setup, signups) = BuildTiamatBoard(firstMemberLocked: true);

        var html = EventBoardHtmlBuilder.Build(ev, setup, signups, Array.Empty<EventSignupLine>());

        Assert.Contains(LockedName, html);            // 🔒 marker on the locked member
        Assert.DoesNotContain(UnlockedName, html);    // but NOT on the unlocked member
        Assert.Contains(StayingTileLabel, html);      // the "Staying" stat tile
        Assert.Contains(LockGlyph + " 1", html);      // tile value = the locked count
        Assert.Contains("Staying next window", html); // the legend key
    }

    [Fact]
    public void Build_WindowHnm_NoLocks_OmitsMarkerAndTile()
    {
        var (ev, setup, signups) = BuildTiamatBoard(firstMemberLocked: false);

        var html = EventBoardHtmlBuilder.Build(ev, setup, signups, Array.Empty<EventSignupLine>());

        Assert.DoesNotContain(LockedName, html);      // no per-slot 🔒 marker
        Assert.DoesNotContain(UnlockedName, html);
        Assert.DoesNotContain(StayingTileLabel, html); // no "Staying" tile when count is 0
        // The legend key still appears on any window HNM (like the always-shown crown key).
        Assert.Contains("Staying next window", html);
    }
}
