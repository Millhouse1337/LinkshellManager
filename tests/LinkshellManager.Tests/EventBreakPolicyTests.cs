using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The Break Room applies to TIMED events only. A windowed (HNM) camp credits attendance per posted
// window, so there is no clock to pause — EventBreakPolicy is the single predicate every surface
// asks instead of re-deriving its own `windowCount > 1` test.
public class EventBreakPolicyTests
{
    private static Event Ev(
        string eventName,
        string? eventType = "Event",
        string? assignedMonster = null,
        int? windowCountOverride = null) => new()
    {
        EventName = eventName,
        EventType = eventType,
        AssignedMonsterName = assignedMonster,
        WindowCountOverride = windowCountOverride,
    };

    // Curated multi-window monsters: no Break Room, however the event was named.
    [Theory]
    [InlineData("Tiamat")]
    [InlineData("Jormungand")]
    [InlineData("Vrtra")]
    [InlineData("Fafnir")]
    [InlineData("Nidhogg")]
    [InlineData("Behemoth")]
    [InlineData("King Behemoth")]
    [InlineData("Adamantoise")]
    [InlineData("Aspidochelone")]
    [InlineData("Goblin Furrier")]
    [InlineData("Adamantoise/Aspidochelone")]
    [InlineData("Fafnir/Nidhogg")]
    [InlineData("Behemoth/King Behemoth")]
    public void WindowedMonsters_HaveNoBreakRoom(string monster)
    {
        var ev = Ev(monster, assignedMonster: monster);
        Assert.True(EventBreakPolicy.IsWindowedAttendance(ev));
        Assert.False(EventBreakPolicy.SupportsBreakRoom(ev));
    }

    // A plain timed event keeps every break feature — this is the case the Break Room exists for.
    [Theory]
    [InlineData("Dynamis-Xarcabard", "Event")]
    [InlineData("Sky Farm", "Sky")]
    [InlineData("Sea Camp", "Sea")]
    public void TimedEvents_KeepTheBreakRoom(string name, string type)
    {
        var ev = Ev(name, type);
        Assert.False(EventBreakPolicy.IsWindowedAttendance(ev));
        Assert.True(EventBreakPolicy.SupportsBreakRoom(ev));
    }

    // Decision A, locked: EventType "HNM" is folded in fail-closed. Razor used to gate on the type
    // while the Activity and addon gated on the count; OR-ing them means an HNM-typed board with a
    // single window (unknown monster, no override) still loses its break controls rather than
    // newly gaining them.
    [Theory]
    [InlineData("HNM")]
    [InlineData("hnm")]
    [InlineData("Hnm")]
    public void HnmEventType_HasNoBreakRoom_EvenAtOneWindow(string type)
    {
        var ev = Ev("Some Unknown Mob", type);
        Assert.Equal(1, DiscordEventMessageBuilder.EffectiveWindowCount(ev));
        Assert.True(EventBreakPolicy.IsWindowedAttendance(ev));
    }

    // The addon's NMS / HENM preset categories send a multi-window event with a NON-"HNM" type.
    // Razor's old `EventType == "HNM"` test let these keep their break controls; the count arm of
    // the OR is what closes that.
    [Fact]
    public void MultiWindowWithNonHnmType_HasNoBreakRoom()
    {
        var ev = Ev("Old Sabertooth Tiger", "Event", windowCountOverride: 2);
        Assert.False(string.Equals(ev.EventType, "HNM", StringComparison.OrdinalIgnoreCase));
        Assert.True(EventBreakPolicy.IsWindowedAttendance(ev));
    }

    // THE test that proves max-of-both-chains is load-bearing. The display chain reads
    // AssignedMonsterName first and sees a custom mob (1 window); the credit chain reads EventName
    // and sees Fafnir (2). A gate built on either chain alone would disagree with the credit path
    // that actually pays this event out.
    [Fact]
    public void DivergentWindowCountChains_GateOnTheLargerOne()
    {
        var ev = Ev("Fafnir", "Event", assignedMonster: "Custom Thing");

        var displayChain = DiscordEventMessageBuilder.EffectiveWindowCount(ev);
        var creditChain = ev.WindowCountOverride ?? HnmConfig.GetWindowCount(ev.EventName);

        Assert.Equal(1, displayChain);
        Assert.True(creditChain > 1, "EventName 'Fafnir' must resolve multi-window on the credit chain.");
        Assert.Equal(creditChain, EventBreakPolicy.GatingWindowCount(ev));
        Assert.True(EventBreakPolicy.IsWindowedAttendance(ev));
    }

    // The mirror of the above: display chain larger than credit chain still gates.
    [Fact]
    public void CadenceOnlyWindowCount_StillGates()
    {
        var ev = Ev("Tuesday Night Run", "Event", assignedMonster: "Tiamat");

        Assert.Equal(1, ev.WindowCountOverride ?? HnmConfig.GetWindowCount(ev.EventName));
        Assert.Equal(25, DiscordEventMessageBuilder.EffectiveWindowCount(ev));
        Assert.True(EventBreakPolicy.IsWindowedAttendance(ev));
    }

    // An EXPLICIT override of 1 does un-window a curated monster, and that is correct: the override
    // short-circuits both chains, so EndEventCoreAsync's `isWindowed` is false and the event is paid
    // by accumulated DURATION. A timed payout needs a pausable clock, so the Break Room must stay
    // available — gating it off here would strand members with no way to stop time-based DKP.
    //
    // The gate deliberately tracks the credit path rather than the monster's name. Note the addon
    // can't reach this state: AddonApiController.AddonEvents stores `WindowCount is > 1 ? ... : null`,
    // so a 1 arrives as null and the cadence arm still reports 25.
    [Fact]
    public void ExplicitWindowCountOverrideOfOne_IsTimed_SoBreaksApply()
    {
        var ev = Ev("Tiamat", "Event", assignedMonster: "Tiamat", windowCountOverride: 1);

        Assert.Equal(1, EventBreakPolicy.GatingWindowCount(ev));
        Assert.False(EventBreakPolicy.IsWindowedAttendance(ev));
        Assert.True(EventBreakPolicy.SupportsBreakRoom(ev));
    }

    // ...but the same event typed "HNM" still loses breaks, because the type arm of the OR catches
    // it. This pins the interaction between the two arms.
    [Fact]
    public void ExplicitOverrideOfOne_StillGatedWhenTypedHnm()
    {
        var ev = Ev("Tiamat", "HNM", assignedMonster: "Tiamat", windowCountOverride: 1);
        Assert.True(EventBreakPolicy.IsWindowedAttendance(ev));
    }

    [Fact]
    public void GatingWindowCount_IsClampedToMaxWindow()
    {
        var ev = Ev("Tiamat", "Event", assignedMonster: "Tiamat", windowCountOverride: 9999);
        Assert.Equal(HnmConfig.MaxWindow, EventBreakPolicy.GatingWindowCount(ev));
    }
}

// The same predicate now also decides how far the board's "Withdraw" button goes
// (DiscordInteractionsController.HandlePartySlotLeaveAsync). On a windowed camp Withdraw is a FULL
// exit — slot and attendance both — because there is no accruing clock for a kept attendance row to
// protect, and keeping one parked the member under "Also Attending" where they still read as signed
// up. On a duration-paid event the live case must keep it, or withdrawing destroys earned DKP.
public class WithdrawScopeTests
{
    private static Event Ev(string name, string? type, string? monster = null, int? windowCountOverride = null) => new()
    {
        EventName = name,
        EventType = type,
        AssignedMonsterName = monster,
        WindowCountOverride = windowCountOverride,
    };

    // Withdraw on these removes the attendance row too, even mid-camp.
    [Theory]
    [InlineData("Tiamat", "HNM", "Tiamat")]
    [InlineData("Jormungand", "HNM", "Jormungand")]
    [InlineData("Vrtra", "HNM", "Vrtra")]
    [InlineData("Fafnir", "HNM", "Fafnir")]
    public void WindowedCamp_WithdrawIsAFullExit(string name, string type, string monster) =>
        Assert.True(EventBreakPolicy.IsWindowedAttendance(Ev(name, type, monster)));

    // ...and on these it still only frees the slot while live, so accrued DKP survives.
    [Theory]
    [InlineData("Dynamis-Xarcabard", "Event")]
    [InlineData("Sky Farm", "Sky")]
    [InlineData("Sea Camp", "Sea")]
    public void TimedEvent_WithdrawKeepsAttendanceWhileLive(string name, string type) =>
        Assert.False(EventBreakPolicy.IsWindowedAttendance(Ev(name, type)));

    // The one case that pays by DURATION despite a windowed monster's name — an explicit override of
    // 1. Withdraw must NOT strip attendance there, or a live member loses time-based DKP.
    [Fact]
    public void ExplicitOverrideOfOne_KeepsAttendance_BecauseItIsPaidByDuration() =>
        Assert.False(EventBreakPolicy.IsWindowedAttendance(
            Ev("Tiamat", "Event", "Tiamat", windowCountOverride: 1)));
}
