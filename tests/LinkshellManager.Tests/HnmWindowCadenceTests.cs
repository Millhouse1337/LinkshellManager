using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Built-in per-monster window cadence + count, the single source of truth for automatic HNM window
// advance. The wyrms run 25 × 60min; the kings/dragons run 7 × 10min (window 1 at the pop through
// window 7 an hour later). Everything else advances manually.
public class HnmWindowCadenceTests
{
    [Theory]
    [InlineData("Tiamat", 60, 25)]
    [InlineData("Jormungand", 60, 25)]
    [InlineData("Vrtra", 60, 25)]
    [InlineData("Adamantoise", 10, 7)]
    [InlineData("Aspidochelone", 10, 7)]
    [InlineData("Behemoth", 10, 7)]
    [InlineData("King Behemoth", 10, 7)]
    [InlineData("Fafnir", 10, 7)]
    [InlineData("Nidhogg", 10, 7)]
    // Timed NMs riding the kings/dragons' short band.
    [InlineData("Capricious Cassie", 10, 7)]
    [InlineData("Bune", 10, 7)]
    [InlineData("Boroka", 10, 7)]
    [InlineData("Roc", 10, 7)]
    public void DefaultWindowCadence_KnownHnms(string monster, int minutes, int windows)
    {
        var cadence = HnmConfig.DefaultWindowCadence(monster);
        Assert.NotNull(cadence);
        Assert.Equal(minutes, cadence!.Value.Minutes);
        Assert.Equal(windows, cadence.Value.Windows);
    }

    // Only the 25-window wyrms clear the roster on "Next Window". The 7-window kings/dragons are
    // ONE camp marching at 10-minute steps — stepping the counter must not throw their roster away.
    [Theory]
    [InlineData("Tiamat", true)]
    [InlineData("Jormungand", true)]
    [InlineData("Vrtra", true)]
    [InlineData("Fafnir", false)]
    [InlineData("Nidhogg", false)]
    [InlineData("Fafnir/Nidhogg", false)]
    [InlineData("Behemoth", false)]
    [InlineData("King Behemoth", false)]
    [InlineData("Behemoth/King Behemoth", false)]
    [InlineData("Adamantoise", false)]
    [InlineData("Aspidochelone", false)]
    [InlineData("Adamantoise/Aspidochelone", false)]
    [InlineData("Kirin", false)]
    [InlineData(null, false)]
    public void WindowAdvanceWipesRoster_WyrmsOnly(string? monster, bool wipes) =>
        Assert.Equal(wipes, HnmConfig.WindowAdvanceWipesRoster(monster));

    // (The ManualNextShouldStep tests lived here. They covered the interplay between an officer's
    // "Next Window" press and the timed cadence — which window a press stepped to, and when a press
    // only settled a turnover already underway. Both buttons that could move the counter are gone;
    // the cadence is now the only thing that advances a window, so there is no interplay left to
    // pin down. ScheduledWindow below is the whole story.)

    // The wyrm board wipes the instant its window changes: zero grace means the clear rides the same
    // background tick that moves the counter, so the new window number and the empty roster reach the
    // board in one edit. Any non-zero value reintroduces a gap where the board shows "Window N" over
    // window N-1's signups — which is exactly what the old 5-minute grace did, for most of an hour.
    [Fact]
    public void WindowClearGrace_IsZero_SoTheWipeRidesTheWindowChange() =>
        Assert.Equal(TimeSpan.Zero, HnmConfig.WindowClearGrace);

    // The clear condition is `now >= windowOpenedAt + grace`. At zero grace that is already true the
    // moment the counter advances, so the advance and the wipe can never land on different ticks.
    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(25)]
    public void WindowOpening_IsImmediatelyClearable(int window)
    {
        var anchor = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var windowOpenedAt = anchor.AddMinutes((window - 1) * 60);
        Assert.True(windowOpenedAt >= windowOpenedAt + HnmConfig.WindowClearGrace);
    }

    // Every 7-window camp keeps its roster — stated against the cadence table so adding a monster
    // to ShortWindowHnms can't quietly opt it into wiping.
    [Fact]
    public void SevenWindowCamps_NeverWipe()
    {
        foreach (var setup in HnmConfig.WindowedHnmSetups())
        {
            if (setup.Windows == 7)
            {
                Assert.False(HnmConfig.WindowAdvanceWipesRoster(setup.Monster), setup.Monster);
            }
        }
    }

    [Theory]
    [InlineData("Adamantoise/Aspidochelone", 10, 7)] // combined "Base/Stronger" label resolves
    [InlineData("Behemoth/King Behemoth", 10, 7)]
    public void DefaultWindowCadence_CombinedLabel(string monster, int minutes, int windows)
    {
        var cadence = HnmConfig.DefaultWindowCadence(monster);
        Assert.NotNull(cadence);
        Assert.Equal(minutes, cadence!.Value.Minutes);
        Assert.Equal(windows, cadence.Value.Windows);
    }

    [Theory]
    [InlineData("Goblin Furrier")] // a Testing monster — advances manually, no timed cadence
    [InlineData("Despot")]          // a sky-farm NM — not a windowed HNM
    [InlineData("Some Random NM")]
    [InlineData(null)]
    public void DefaultWindowCadence_NonTimed_IsNull(string? monster)
    {
        Assert.Null(HnmConfig.DefaultWindowCadence(monster));
    }

    // A 7-window Adamantoise must NOT label its later windows "Close" — the named pair is only for
    // a genuine 2-post camp. Everything else is plain numbered "Window N".
    [Fact]
    public void GetDefaultWindowLabel_SevenWindows_IsNumbered()
    {
        Assert.Null(HnmConfig.GetDefaultWindowLabel("Adamantoise", 1, effectiveWindowCount: 7));
        Assert.Null(HnmConfig.GetDefaultWindowLabel("Adamantoise", 5, effectiveWindowCount: 7));
    }

    // The two posts a king/dragon camp actually takes: one at open, one at close.
    [Fact]
    public void GetDefaultWindowLabel_TwoWindows_IsOpenThenClose()
    {
        Assert.Equal("Open", HnmConfig.GetDefaultWindowLabel("Goblin Furrier", 1, effectiveWindowCount: 2));
        Assert.Equal("Close", HnmConfig.GetDefaultWindowLabel("Goblin Furrier", 2, effectiveWindowCount: 2));
    }

    // Windows posted before the rename are stored with the old names. They must READ as the new
    // ones, or a camp that was live when this shipped shows "On Time" beside "Close".
    [Theory]
    [InlineData("On Time", "Open")]
    [InlineData("Claim/Kill", "Close")]
    [InlineData("  on time  ", "Open")]      // trimmed + case-insensitive
    [InlineData("Open", "Open")]             // already migrated: unchanged
    [InlineData("Window 5", "Window 5")]     // a numbered label passes through
    [InlineData(null, null)]                 // null stays null (view falls back to "Window N")
    public void NormalizeWindowLabel_MapsLegacyNames(string? stored, string? expected) =>
        Assert.Equal(expected, HnmConfig.NormalizeWindowLabel(stored));

    // The effective count/cadence helpers every caller now uses. Built in per monster — there is
    // no per-linkshell override path, so the same monster resolves the same way everywhere.
    [Theory]
    [InlineData("Tiamat", 25, 60)]
    [InlineData("Jormungand", 25, 60)]
    [InlineData("Vrtra", 25, 60)]
    // The ToAU three cover the wyrms' same 24-hour band in five six-hour windows. They are ALSO
    // members of LongWindowHnms, so this pins the one thing that keeps them off 25 x 60: every
    // resolver testing ToauHnms first.
    [InlineData("Cerberus", 5, 360)]
    [InlineData("Hydra", 5, 360)]
    [InlineData("Khimaira", 5, 360)]
    [InlineData("Adamantoise", 7, 10)]
    [InlineData("Fafnir", 7, 10)]
    [InlineData("King Behemoth", 7, 10)]
    [InlineData("Adamantoise/Aspidochelone", 7, 10)] // combined "Base/Stronger" label resolves
    public void EffectiveWindows_KnownHnms(string monster, int windows, int minutes)
    {
        Assert.Equal(windows, HnmConfig.EffectiveWindowCount(monster));
        Assert.Equal(minutes, HnmConfig.WindowAdvanceMinutes(monster));
    }

    // Testing presets keep their 2-window "On Time" / "Claim/Kill" shape and advance only when an
    // officer clicks Next Window — they're deliberately off the timed cadence.
    [Fact]
    public void EffectiveWindows_TestingMonster_IsTwoWindowsManual()
    {
        Assert.Equal(2, HnmConfig.EffectiveWindowCount("Goblin Furrier"));
        Assert.Equal(0, HnmConfig.WindowAdvanceMinutes("Goblin Furrier"));
    }

    [Theory]
    [InlineData("Despot")]          // a sky-farm NM — not a windowed HNM
    [InlineData("Some Random NM")]
    [InlineData(null)]
    public void EffectiveWindows_NonHnm_IsSingleWindowManual(string? monster)
    {
        Assert.Equal(1, HnmConfig.EffectiveWindowCount(monster));
        Assert.Equal(0, HnmConfig.WindowAdvanceMinutes(monster));
    }

    // The canonical enumeration of the built-in bands. Covers exactly the monsters on a timed
    // cadence — the three wyrms, the ToAU three on their own long band, the six kings/dragons and
    // the four timed NMs that share the short band — no Testing presets, most windows first.
    [Fact]
    public void WindowedHnmSetups_CoversEveryTimedHnm_LongestBandFirst()
    {
        var setups = HnmConfig.WindowedHnmSetups();

        Assert.Equal(16, setups.Count);
        Assert.Equal(
            new[] { "Jormungand", "Tiamat", "Vrtra" },
            setups.Where(s => s.Windows == 25).Select(s => s.Monster).ToArray());
        Assert.Equal(
            new[]
            {
                "Adamantoise", "Aspidochelone", "Behemoth", "Boroka", "Bune", "Capricious Cassie",
                "Fafnir", "King Behemoth", "Nidhogg", "Roc",
            },
            setups.Where(s => s.Windows == 7).Select(s => s.Monster).ToArray());
        // Sorted by window COUNT, not by how long the band runs: the ToAU three cover a full 24
        // hours — as long as the wyrms — and still come last on five windows.
        Assert.Equal(
            new[] { "Cerberus", "Hydra", "Khimaira" },
            setups.Where(s => s.Windows == 5).Select(s => s.Monster).ToArray());

        // Most windows first.
        Assert.Equal(setups.OrderByDescending(s => s.Windows).Select(s => s.Monster), setups.Select(s => s.Monster));
        // Every entry carries the cadence its monster actually advances on.
        Assert.All(setups, s => Assert.Equal(HnmConfig.WindowAdvanceMinutes(s.Monster), s.Minutes));
        Assert.All(setups, s => Assert.Equal(HnmConfig.EffectiveWindowCount(s.Monster), s.Windows));
        // Testing presets are deliberately absent — they have no timed cadence.
        Assert.DoesNotContain(setups, s => s.Monster == "Goblin Furrier");
    }
}

// The number the board prints against the roster printed underneath it: the window being AWAITED,
// on every board that has one. A window is a knife edge — the pop chance is spent at the boundary
// and the roster is wiped on that tick — so the names on a board are the roster signing up for the
// next chance, not a record of the one that just went by.
//
// Wyrms were excepted from this and named the OPENED window, which is how a Tiamat board came to
// read "Window 7 of 25" twenty minutes after window 7 had passed, directly above its own "Next
// window 8" countdown and a roster that had been cleared for window 8.
public class FocusWindowTests
{
    private static Event Camp(string monster, int window, string? mode = null, bool hasNext = true) => new()
    {
        EventType = "HNM",
        EventName = monster,
        AssignedMonsterName = monster,
        AttendanceMode = mode,
        HnmWindowNumber = window,
        NextWindowAt = hasNext ? new DateTime(2026, 8, 6, 11, 27, 56, DateTimeKind.Utc) : null,
    };

    // A wyrm board names the window its cleared roster is signing up FOR — the awaited one.
    [Theory]
    [InlineData("Tiamat", 1, 2)]
    [InlineData("Jormungand", 1, 2)]
    [InlineData("Vrtra", 1, 2)]
    [InlineData("Tiamat", 2, 3)]
    [InlineData("Tiamat", 7, 8)]
    public void WipingBoard_NamesTheAwaitedWindow(string monster, int window, int expected) =>
        Assert.Equal(expected, DiscordEventMessageBuilder.FocusWindow(Camp(monster, window)));

    // The heading can never name a window the board itself reports as already passed. That line is
    // dated off the OPENED window, so this is the invariant the old wyrm exception broke.
    [Theory]
    [InlineData("Tiamat", 7)]
    [InlineData("Fafnir", 3)]
    public void FocusWindow_IsNeverAWindowAlreadyPassed(string monster, int window)
    {
        var camp = Camp(monster, window);
        Assert.True(DiscordEventMessageBuilder.FocusWindow(camp) > DiscordEventMessageBuilder.OpenedWindow(camp));
    }

    // ...and the kings/dragons, which never wipe, name it the same way.
    [Theory]
    [InlineData("Fafnir", 1, 2)]
    [InlineData("Behemoth", 3, 4)]
    [InlineData("Adamantoise", 6, 7)]
    public void NonWipingBoard_StillNamesTheAwaitedWindow(string monster, int window, int expected) =>
        Assert.Equal(expected, DiscordEventMessageBuilder.FocusWindow(Camp(monster, window)));

    // A wyrm in Manual Check In mode doesn't wipe either (members X-in per window themselves), so
    // it keeps the awaited-window number the Check In button records against.
    [Fact]
    public void WyrmInManualCheckInMode_NamesTheAwaitedWindow() =>
        Assert.Equal(2, DiscordEventMessageBuilder.FocusWindow(
            Camp("Tiamat", 1, mode: HnmAttendanceModes.Wd)));

    // No next window to await — the awaited number collapses back onto the current one either way.
    [Theory]
    [InlineData("Tiamat", 25)]
    [InlineData("Fafnir", 7)]
    public void FinalWindow_Collapses(string monster, int window) =>
        Assert.Equal(window, DiscordEventMessageBuilder.FocusWindow(Camp(monster, window)));

    [Fact]
    public void NoNextWindowAt_Collapses() =>
        Assert.Equal(3, DiscordEventMessageBuilder.FocusWindow(Camp("Fafnir", 3, hasNext: false)));

    // The heading's condition and the advancer's clear condition are one method, so they cannot
    // drift into disagreeing about which boards wipe.
    //
    // The MONSTER decides it, not the attendance mode: a Manual Check In wyrm re-forms its camp
    // every hour like any other wyrm, so it wipes too (its check-in ledger survives the wipe —
    // see HnmWindowRosterSnapshotTests).
    [Theory]
    [InlineData("Tiamat", null, true)]
    [InlineData("Vrtra", null, true)]
    [InlineData("Tiamat", HnmAttendanceModes.Wd, true)]
    [InlineData("Jormungand", HnmAttendanceModes.Wd, true)]
    [InlineData("Fafnir", null, false)]                       // one continuous camp at 10-min steps
    [InlineData("Fafnir", HnmAttendanceModes.Wd, false)]
    [InlineData("King Behemoth", null, false)]
    public void ClearsRosterOnWindowAdvance_MatchesTheAdvancersGate(string monster, string? mode, bool wipes) =>
        Assert.Equal(wipes, DiscordEventMessageBuilder.ClearsRosterOnWindowAdvance(Camp(monster, 2, mode)));

    // The countdown line is driven by HasNextWindow, not by the heading number, and the two now
    // name the same window on a wiping board: the heading says which window the camp is on, the
    // countdown says when it opens. They part company only on the final window, where the heading
    // collapses onto the opened one and there is no countdown left to print.
    [Fact]
    public void WipingBoard_HeadsTheSameWindowItCountsDownTo()
    {
        var tiamat = Camp("Tiamat", 1);
        Assert.True(DiscordEventMessageBuilder.HasNextWindow(tiamat));
        Assert.Equal(2, DiscordEventMessageBuilder.FocusWindow(tiamat));      // heading
        Assert.Equal(2, DiscordEventMessageBuilder.OpenedWindow(tiamat) + 1); // countdown

        var last = Camp("Tiamat", 25);
        Assert.False(DiscordEventMessageBuilder.HasNextWindow(last));
        Assert.Equal(25, DiscordEventMessageBuilder.FocusWindow(last));
    }
}
