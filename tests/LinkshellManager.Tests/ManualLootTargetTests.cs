using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Where a hand-entered loot row gets filed.
//
// Loot used to carry a free-text "context" string that the server turned into a throwaway ToD, which
// is why every manually added drop showed up in the Loot History as source "ToD" with no event
// behind it. It now points at a real Event, a real EventHistory, or at nothing — and which of the
// three is decided entirely by this parse, off values that arrive from a client.
public class ManualLootTargetTests
{
    [Fact]
    public void Live_PicksTheEventId_AndLeavesHistoryAlone()
    {
        var target = ManualLootTarget.Parse("live", eventId: 42, eventHistoryId: 99);

        Assert.Equal(ManualLootTargetKind.LiveEvent, target.Kind);
        Assert.Equal(42, target.EventId);
        Assert.Null(target.EventHistoryId);
    }

    [Fact]
    public void Past_PicksTheHistoryId_AndLeavesTheEventAlone()
    {
        var target = ManualLootTarget.Parse("past", eventId: 42, eventHistoryId: 99);

        Assert.Equal(ManualLootTargetKind.PastEvent, target.Kind);
        Assert.Equal(99, target.EventHistoryId);
        Assert.Null(target.EventId);
    }

    [Fact]
    public void None_CarriesNeitherId()
    {
        var target = ManualLootTarget.Parse("none", eventId: 42, eventHistoryId: 99);

        Assert.Equal(ManualLootTargetKind.None, target.Kind);
        Assert.Null(target.EventId);
        Assert.Null(target.EventHistoryId);
    }

    // A kind with no usable id falls back to None rather than to a half-formed target. The loot is
    // still recorded and still charged — it just is not filed under an event, which an officer can
    // see and fix. Silently keeping "live" with a null id would send it to a lookup that fails.
    [Theory]
    [InlineData("live", null, null)]
    [InlineData("live", 0, null)]
    [InlineData("past", null, null)]
    [InlineData("past", null, 0)]
    public void AKindWithoutAUsableId_FallsBackToNone(string kind, int? eventId, int? historyId)
        => Assert.Equal(ManualLootTargetKind.None, ManualLootTarget.Parse(kind, eventId, historyId).Kind);

    // Unrecognised input is None, never an event. Case and padding are tolerated because these
    // values come off a <select> and a JSON body, not from a closed enum on the wire.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Other")]
    [InlineData("tod")]
    public void UnrecognisedKinds_AreNone(string? kind)
        => Assert.Equal(ManualLootTargetKind.None, ManualLootTarget.Parse(kind, 42, 99).Kind);

    [Theory]
    [InlineData("LIVE")]
    [InlineData("  Live  ")]
    public void KindMatchingIsCaseAndWhitespaceTolerant(string kind)
        => Assert.Equal(ManualLootTargetKind.LiveEvent, ManualLootTarget.Parse(kind, 42, null).Kind);

    // THE double-charge guard, at the model level.
    //
    // Ordinary event loot is charged when the event CLOSES. Hand-entered loot is charged on the
    // spot and stamps DkpDebitedAt; both close paths skip a stamped row. A new row defaulting to
    // anything but null would mean event-awarded loot silently never gets charged at all.
    [Fact]
    public void ANewEventLootRow_IsNotYetDebited()
        => Assert.Null(new EventLootDetail().DkpDebitedAt);

    // A "No event" row has no Event and no EventHistory to reach a linkshell through, which is the
    // whole reason EventLootDetail carries its own LinkshellId now.
    [Fact]
    public void ANewEventLootRow_HasNoLinkshellUntilOneIsSet()
        => Assert.Null(new EventLootDetail().LinkshellId);
}
