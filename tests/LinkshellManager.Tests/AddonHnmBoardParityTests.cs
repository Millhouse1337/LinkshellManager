using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// A sign-up board created from the LSM addon must be the SAME board as one created in the app:
// same window traversal, same buttons.
//
// The bug was one column doing two jobs. Event.WindowCountOverride holds the count of attendance
// POSTS a camp takes — 2 on a king/dragon, an Open and a Close — and EffectiveWindowCount used to
// read it FIRST when answering "how many SPAWN windows does this camp run", which is a different
// number (7). So an addon-made camp, whose override correctly said 2, produced a board reading
// "Window N of 2" that HnmWindowAdvanceBackgroundService stopped advancing five windows early.
//
// The fix is that the spawn count is derived from the monster's cadence and never read out of that
// column, so both numbers can be right at once. These tests pin all three claims: the spawn count,
// the post count staying separate, and the buttons.
public class AddonHnmBoardParityTests
{
    // Mirrors DiscordBotClient.JsonOptions (camelCase + drop nulls) so assertions see the wire JSON.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly DateTime CampStart = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    // A one-party board, the smallest thing that renders the full button set.
    private static (PartySetup Setup, Dictionary<int, EventPartySlotSignup> Signups) BuildSetup(int eventId)
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
            Id = 31, Name = "HNM",
            Alliances = new List<PartySetupAlliance>
            {
                new() { Id = 21, PartySetupId = 31, SortOrder = 0, Name = "Alliance 1", Parties = new List<PartySetupParty> { party } },
            },
        };
        var signups = new Dictionary<int, EventPartySlotSignup>
        {
            [101] = new()
            {
                Id = 1, EventId = eventId, PartySetupSlotId = 101,
                CharacterName = "Millh", Role = "DPS", MainJob = "BLM", SubJob = "RDM",
            },
        };
        return (setup, signups);
    }

    // A live King Behemoth camp. `creationSource` is the ONLY thing that differs between the addon
    // and app versions once both are seeded the same way.
    private static Event Camp(int id, string? creationSource, int? windowCountOverride) => new()
    {
        Id = id,
        EventName = "Behemoth/King Behemoth D1",
        EventType = "HNM",
        AssignedMonsterName = "Behemoth/King Behemoth",
        DayNumber = 1,
        StartTime = CampStart,
        CommencementStartTime = CampStart,
        WindowAnchorAt = CampStart,
        HnmWindowNumber = 3,
        NextWindowAt = CampStart.AddMinutes(30),
        CreationSource = creationSource,
        WindowCountOverride = windowCountOverride,
    };

    private static string ComponentsJson(Event ev)
    {
        var (setup, signups) = BuildSetup(ev.Id);
        var payload = DiscordEventMessageBuilder.Build(ev, Array.Empty<EventSignupLine>(), setup, signups);
        return JsonSerializer.Serialize(payload, Wire);
    }

    // THE regression, and the exact shape of it: a camp carrying the addon's 2-POST count still
    // traverses all 7 SPAWN windows, because the spawn count comes off the monster's cadence rather
    // than out of that column. Both numbers are right at the same time.
    [Fact]
    public void AddonCamp_KeepsItsPostCount_AndStillRunsTheFullSpawnCycle()
    {
        var addon = Camp(1, "Addon", windowCountOverride: 2);

        Assert.Equal(7, DiscordEventMessageBuilder.EffectiveWindowCount(addon));
        // The post count is untouched — it is what the addon labels Open / Close from.
        Assert.Equal(2, addon.WindowCountOverride);
        Assert.Equal(2, HnmConfig.GetWindowCount(addon.AssignedMonsterName));
    }

    // Whoever filed the camp, the board traverses the same spawn cycle. An app-made camp stores the
    // spawn count in that column (HnmEventSeeder stamps it) and an addon-made one stores its post
    // count, and the board must not be able to tell.
    [Fact]
    public void AddonAndAppCamps_TraverseTheSameSpawnCycle()
    {
        var addon = Camp(1, "Addon", windowCountOverride: 2);
        var app = Camp(2, null, HnmConfig.EffectiveWindowCount("Behemoth/King Behemoth"));

        Assert.Equal(
            DiscordEventMessageBuilder.EffectiveWindowCount(app),
            DiscordEventMessageBuilder.EffectiveWindowCount(addon));
    }

    // The post count is what NAMES a window, and it must not depend on where the camp was filed.
    // An app-made camp stores the spawn count in WindowCountOverride and an addon-made one stores
    // its post count, so reading that column directly gave the same Behemoth camp "Open"/"Close"
    // or "Window 1..7" depending on its origin.
    [Fact]
    public void PostCount_ComesFromTheMonster_NotFromWhoFiledTheCamp()
    {
        var addon = Camp(1, "Addon", windowCountOverride: 2);
        var app = Camp(2, null, HnmConfig.EffectiveWindowCount("Behemoth/King Behemoth"));

        Assert.Equal(2, DiscordEventMessageBuilder.AttendancePostCount(addon));
        Assert.Equal(2, DiscordEventMessageBuilder.AttendancePostCount(app));
    }

    // The pair, on one camp: 7 pop chances, 2 roster reads. The whole point of keeping them apart.
    [Fact]
    public void SpawnAndPostCounts_AreBothRightOnTheSameCamp()
    {
        var camp = Camp(1, "Addon", windowCountOverride: 2);

        Assert.Equal(7, DiscordEventMessageBuilder.EffectiveWindowCount(camp));
        Assert.Equal(2, DiscordEventMessageBuilder.AttendancePostCount(camp));
    }

    // ...and the post count is what makes the windows read Open / Close. The spawn count sends
    // GetDefaultWindowLabel down its numbered branch, which is the "Window 1" in the Activity's
    // Attendance Windows card that this fixes.
    [Fact]
    public void PostCount_NamesTheWindowsOpenAndClose()
    {
        var camp = Camp(1, "Addon", windowCountOverride: 2);
        var posts = DiscordEventMessageBuilder.AttendancePostCount(camp);

        Assert.Equal("Open", HnmConfig.GetDefaultWindowLabel(camp.EventName, 1, posts));
        Assert.Equal("Close", HnmConfig.GetDefaultWindowLabel(camp.EventName, 2, posts));
        // The spawn count is exactly what used to leave these unnamed.
        Assert.Null(HnmConfig.GetDefaultWindowLabel(
            camp.EventName, 1, DiscordEventMessageBuilder.EffectiveWindowCount(camp)));
    }

    // A wyrm posts once per hour-long window, so its windows stay numbered on both scales. Pins
    // that "post count" does not just mean "2".
    //
    // Posting once per window makes the two counts EQUAL here — 25 posts across 25 windows — and
    // the stale 24 that used to sit in GetWindowCount is exactly what made the Attendance Windows
    // card read "of 24" under a board that said "Window N of 25". An addon-made camp's stored
    // override still says 24; the monster overrules it, which is what keeps old rows correct.
    [Fact]
    public void Wyrm_PostsPerWindow_AndStaysNumbered()
    {
        var camp = Camp(1, "Addon", windowCountOverride: 24);
        camp.AssignedMonsterName = "Tiamat";

        Assert.Equal(25, DiscordEventMessageBuilder.EffectiveWindowCount(camp));
        Assert.Equal(25, DiscordEventMessageBuilder.AttendancePostCount(camp));
        Assert.Null(HnmConfig.GetDefaultWindowLabel(
            camp.EventName, 1, DiscordEventMessageBuilder.AttendancePostCount(camp)));
    }

    // A non-curated monster has no monster-derived post count, so the override it was created with
    // is the only signal and must survive. This is the NMS-preset case (filed as EventType "HNM").
    [Fact]
    public void NonCuratedMonster_KeepsItsOverrideAsThePostCount()
    {
        var nm = new Event
        {
            Id = 9,
            EventName = "Old Sabertooth Tiger",
            EventType = "HNM",
            StartTime = CampStart,
            CreationSource = "Addon",
            WindowCountOverride = 2,
        };

        Assert.Equal(2, DiscordEventMessageBuilder.AttendancePostCount(nm));
        Assert.Equal("Open", HnmConfig.GetDefaultWindowLabel(nm.EventName, 1, 2));
    }

    // An override of exactly 1 keeps its old meaning — "this camp is NOT windowed, pay it by
    // duration" — and the cadence must not overrule it, or EventBreakPolicy flips the camp onto the
    // windowed payout path and takes its Break Room away. Pinned here because the cadence-first
    // rewrite above is precisely what could break it.
    [Fact]
    public void ExplicitOverrideOfOne_StillUnwindowsACadenceMonster()
    {
        var camp = Camp(1, null, windowCountOverride: 1);

        Assert.Equal(1, DiscordEventMessageBuilder.EffectiveWindowCount(camp));
    }

    // "The same sign up buttons": no button gate anywhere reads CreationSource, so two identically
    // seeded camps render byte-identical component trees. Compared as whole wire JSON rather than by
    // hunting individual custom_ids, so a gate added later that DOES branch on the addon fails here.
    [Fact]
    public void AddonBoard_RendersTheSameButtonsAsAnAppBoard()
    {
        // Same Id on both so the custom_ids (which embed the event id) are comparable, and each side
        // carrying what its own creation path actually stores in that column.
        var addonJson = ComponentsJson(Camp(1, "Addon", windowCountOverride: 2));
        var appJson = ComponentsJson(Camp(1, null, HnmConfig.EffectiveWindowCount("Behemoth/King Behemoth")));

        Assert.Equal(appJson, addonJson);
    }

    // The window row — "View Previous Window" and "End Camp / Enter ToD" — is the row an addon camp
    // was missing whenever it had no monster stamped, because UsesWindows keys on AssignedMonsterName.
    [Fact]
    public void AddonBoard_WithItsMonsterStamped_ShowsTheWindowRow()
    {
        var camp = Camp(1, "Addon", windowCountOverride: 2);

        Assert.True(DiscordEventMessageBuilder.UsesWindows(camp));

        var json = ComponentsJson(camp);
        Assert.Contains(DiscordEventMessageBuilder.ViewPrevWindowPrefix, json, StringComparison.Ordinal);
        Assert.Contains(DiscordEventMessageBuilder.WdPopPrefix, json, StringComparison.Ordinal);
        // The heading is where the traversal is visible to the camp.
        Assert.Contains("Window 4 of 7", json, StringComparison.Ordinal);
    }

    // The mirror: a camp with no monster loses the whole window feature set at once. This is the
    // state the older addon builds left boards in, and what the backfill migration repairs.
    [Fact]
    public void CampWithNoMonster_HasNoWindowRowAtAll()
    {
        var camp = Camp(1, "Addon", windowCountOverride: null);
        camp.AssignedMonsterName = null;

        Assert.False(DiscordEventMessageBuilder.UsesWindows(camp));

        var json = ComponentsJson(camp);
        Assert.DoesNotContain(DiscordEventMessageBuilder.ViewPrevWindowPrefix, json, StringComparison.Ordinal);
        Assert.DoesNotContain(DiscordEventMessageBuilder.WdPopPrefix, json, StringComparison.Ordinal);
    }

    // The else-branch of the new seeding rule. The addon files its NMS presets as EventType "HNM"
    // too, but an NM is not a curated HNM — there is no spawn cadence to look up, so its 2-post count
    // is the only signal there is and must survive untouched.
    [Fact]
    public void NmFiledAsHnmType_KeepsTheAddonsOwnPostCount()
    {
        Assert.False(HnmConfig.IsTrueHnm("Old Sabertooth Tiger"));

        var nm = new Event
        {
            Id = 9,
            EventName = "Old Sabertooth Tiger",
            EventType = "HNM",
            StartTime = CampStart,
            CreationSource = "Addon",
            WindowCountOverride = 2,
        };

        Assert.Equal(2, DiscordEventMessageBuilder.EffectiveWindowCount(nm));
        Assert.False(DiscordEventMessageBuilder.UsesWindows(nm));
    }
}

// The backfill migration's monster table is hand-written SQL against real data, run once, with
// nothing watching. This holds it against HnmConfig so a typo can't write a monster the board is
// unable to resolve — the same guard ChartPopItemMigrationTests puts on the Sky/Sea refiles.
public class AlignAddonHnmCampsMigrationTests
{
    [Fact]
    public void EveryAliasIsACuratedHnm()
    {
        Assert.All(
            AlignAddonHnmCampsWithAppBoards.Monsters,
            m => Assert.True(
                HnmConfig.IsTrueHnm(m.Alias),
                $"'{m.Alias}' is not a curated HNM, so the migration would stamp a monster nothing resolves."));
    }

    // The value written into AssignedMonsterName has to resolve to the same counts as the alias it
    // was matched by — on BOTH scales. A stored name that shifted either one would silently re-cut
    // the camp's board traversal or its Open/Close labelling.
    [Fact]
    public void StoredNameCarriesTheSameCountsAsItsAlias()
    {
        Assert.All(
            AlignAddonHnmCampsWithAppBoards.Monsters,
            m =>
            {
                Assert.Equal(HnmConfig.EffectiveWindowCount(m.Alias), HnmConfig.EffectiveWindowCount(m.Stored));
                Assert.Equal(HnmConfig.GetWindowCount(m.Alias), HnmConfig.GetWindowCount(m.Stored));
            });
    }

    // On a king/dragon the two counts are genuinely different numbers — 2 roster reads across 7
    // spawn windows — which is the whole reason they need separate columns/fields. If that ever
    // collapses, the conflation the tests above guard against stops being detectable.
    //
    // A wyrm is the opposite case and must NOT be asserted apart: it posts once per hour-long
    // window, so 25 == 25 is the correct answer there, and pinning them as distinct is what kept
    // the stale 24 alive in GetWindowCount. Separate fields are still required — being equal on
    // one band doesn't make them one number.
    [Fact]
    public void SpawnAndPostCountsAreDistinct_OnTheKingsAndDragons()
    {
        var byBand = AlignAddonHnmCampsWithAppBoards.Monsters
            .ToLookup(m => HnmConfig.EffectiveWindowCount(m.Stored) == 7);

        Assert.NotEmpty(byBand[true]);
        Assert.All(
            byBand[true],
            m => Assert.NotEqual(HnmConfig.EffectiveWindowCount(m.Stored), HnmConfig.GetWindowCount(m.Stored)));

        // The wyrms, pinned the other way so neither band can drift unnoticed.
        Assert.NotEmpty(byBand[false]);
        Assert.All(
            byBand[false],
            m => Assert.Equal(HnmConfig.EffectiveWindowCount(m.Stored), HnmConfig.GetWindowCount(m.Stored)));
    }

    // AssignedMonsterName is stored in the combined "Base/Stronger" form (HnmConfig.DisplayMonsterName
    // narrows it per day), so every merge-pair alias must map onto the combined label rather than
    // onto itself.
    [Fact]
    public void MergePairAliasesMapOntoTheCombinedLabel()
    {
        foreach (var (baseName, stronger) in HnmConfig.MonsterMergePairs)
        {
            var combined = $"{baseName}/{stronger}";
            foreach (var alias in new[] { baseName, stronger, combined })
            {
                var entry = AlignAddonHnmCampsWithAppBoards.Monsters.SingleOrDefault(m => m.Alias == alias);
                Assert.True(entry.Alias is not null, $"'{alias}' is missing from the migration's monster table.");
                Assert.Equal(combined, entry.Stored);
            }
        }
    }

    // Monsters put on a timed cadence AFTER this migration shipped (2026-08-04). The migration is a
    // one-off backfill over camps that were already on the board when it ran, and it does not re-run
    // — so a monster that did not exist in the app until later can have no camp for it to repair,
    // and adding it to that historical table would be a lie about what the migration did.
    //
    // Anything OLDER than the migration still has to be covered, which is what the test below pins.
    // Extend this list only when you add a monster to the cadence tables, never to silence a real gap.
    private static readonly HashSet<string> CadenceAddedAfterTheMigration =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Capricious Cassie",
            "Bune",
            "Boroka",
            "Roc",
        };

    // Every timed monster that predates the migration is covered — a wyrm left out keeps its
    // 24-window count forever, since the migration is the only thing that repairs a camp already on
    // the board.
    [Fact]
    public void EveryTimedHnmIsCovered()
    {
        var aliases = AlignAddonHnmCampsWithAppBoards.Monsters.Select(m => m.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(
            HnmConfig.WindowedHnmSetups().Where(s => !CadenceAddedAfterTheMigration.Contains(s.Monster)),
            setup => Assert.True(
                aliases.Contains(setup.Monster),
                $"'{setup.Monster}' runs a timed cadence but the migration would never match it."));
    }
}
