using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Which section of the Event System page an event lands in. These have to agree with the Discord
// Activity's liveEvents() / queuedEvents() / isEndedCamp(), because both surfaces read the same
// rows and an officer who sees a camp in Pending on one and Live on the other has no way to tell
// which is lying.
public class EventSystemBucketingTests
{
    private static readonly DateTime Commenced = new(2026, 8, 4, 21, 0, 0, DateTimeKind.Utc);

    private static Event Ev(
        string? type = "Sky",
        DateTime? commencementStartTime = null,
        DateTime? hnmDefeatedAt = null,
        DateTime? wdFinalizedAt = null) => new()
    {
        Id = 1,
        EventType = type,
        CommencementStartTime = commencementStartTime,
        HnmDefeatedAt = hnmDefeatedAt,
        WdFinalizedAt = wdFinalizedAt,
    };

    [Fact]
    public void CommencedNonHnmEvent_IsLive()
    {
        var evt = Ev(commencementStartTime: Commenced);

        Assert.True(EventSystemBuckets.IsLive(evt));
        Assert.False(EventSystemBuckets.IsPending(evt));
    }

    [Fact]
    public void UncommencedEvent_IsPending()
    {
        var evt = Ev();

        Assert.False(EventSystemBuckets.IsLive(evt));
        Assert.True(EventSystemBuckets.IsPending(evt));
    }

    // A defeated Standard board is un-commenced by HnmCampPopService, so this is the path it
    // actually takes: out of Live, into Pending, where its "Edit ToD" button lives.
    [Fact]
    public void DefeatedHnmBoard_UncommencedByPop_IsPending()
    {
        var evt = Ev(type: "HNM", hnmDefeatedAt: Commenced);

        Assert.False(EventSystemBuckets.IsLive(evt));
        Assert.True(EventSystemBuckets.IsPending(evt));
    }

    // Belt-and-braces: if a defeated camp somehow kept its commencement, it must still leave the
    // live board. It then belongs to NEITHER list — deliberately, because that is what the
    // Activity does, and inventing a third home here would be the drift these tests exist to stop.
    [Fact]
    public void DefeatedHnmBoard_StillCommenced_IsNeitherLiveNorPending()
    {
        var evt = Ev(type: "HNM", commencementStartTime: Commenced, hnmDefeatedAt: Commenced);

        Assert.False(EventSystemBuckets.IsLive(evt));
        Assert.False(EventSystemBuckets.IsPending(evt));
    }

    [Fact]
    public void FinalizedManualCheckInCamp_IsNotLive()
    {
        var evt = Ev(type: "HNM", commencementStartTime: Commenced, wdFinalizedAt: Commenced);

        Assert.True(EventSystemBuckets.IsEndedCamp(evt));
        Assert.False(EventSystemBuckets.IsLive(evt));
    }

    // WdFinalizedAt only ends a camp on an HNM board. A non-HNM event carrying the stamp — which
    // shouldn't happen, but the column is nullable on every row — stays live.
    [Fact]
    public void NonHnmEventWithWdFinalizedAt_StaysLive()
    {
        var evt = Ev(type: "Sky", commencementStartTime: Commenced, wdFinalizedAt: Commenced);

        Assert.False(EventSystemBuckets.IsEndedCamp(evt));
        Assert.True(EventSystemBuckets.IsLive(evt));
    }

    [Theory]
    [InlineData("HNM")]
    [InlineData("hnm")]
    [InlineData("  Hnm  ")]
    public void EventTypeMatch_IgnoresCaseAndSurroundingSpace(string type)
        => Assert.True(EventSystemBuckets.IsHnmEvent(Ev(type: type)));

    [Theory]
    [InlineData("Sky")]
    [InlineData("Dynamis")]
    [InlineData(null)]
    [InlineData("")]
    public void EventTypeMatch_RejectsEverythingElse(string? type)
        => Assert.False(EventSystemBuckets.IsHnmEvent(Ev(type: type)));
}
