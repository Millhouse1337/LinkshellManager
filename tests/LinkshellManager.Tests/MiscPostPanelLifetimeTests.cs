using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// HOW LONG A CAPTURE STAYS ON THE ADDON'S MISC POSTS PANEL.
//
// That panel is a live to-do list, not an archive: what you captured at the camp you are standing
// at, and whether an officer has filed it. When the camp ends, every capture on it moves onto a
// review card and the app owns it — there is nothing the addon can do with the row.
//
// Leaving them was actively harmful: the feed is capped at the most recent 50, so finished camps
// pushed the UNFILED captures — the only rows that are anybody's work — off the bottom of it.
//
// The addon used to answer this itself by moving a local cutoff at End Camp. That cutoff lived in
// addon memory, so a reload or a game restart brought every old row back. These pin the rule at the
// place that survives both.
//
// The predicate runs over LINQ-to-Objects here. That tests the RULE, not its SQL translation —
// which is the half that silently regresses when someone adds a status.
public class MiscPostPanelLifetimeTests
{
    private static readonly DateTime Anchor = new(2026, 9, 22, 21, 0, 0, DateTimeKind.Utc);

    private static AttendanceSnapshot Capture(
        string name, WindowEvent? windowEvent = null, Event? liveCamp = null)
        => new()
        {
            Id = Math.Abs(name.GetHashCode() % 10000),
            LinkshellId = 1,
            Name = name,
            CapturedAtUtc = Anchor,
            SnapshotStatus = AttendanceSnapshotStatuses.Active,
            SlotKind = AttendanceSnapshotSlotKinds.Misc,
            WindowEvent = windowEvent,
            WindowEventId = windowEvent?.Id,
            LinkedEvent = liveCamp,
            LinkedEventId = liveCamp?.Id,
        };

    private static WindowEvent LiveCard() => new()
    {
        Id = 10,
        LinkshellId = 1,
        Name = "Fafnir",
        NormalizedName = "FAFNIR",
        Status = WindowEventStatuses.Open,
        CampEndedAtUtc = null,
        FirstCapturedAtUtc = Anchor,
        LastCapturedAtUtc = Anchor,
    };

    // What End Camp produces: Status stays Open, CampEndedAtUtc is stamped. Testing status alone
    // would call this card live — the trap ApplyLiveFilter's own comment warns about.
    private static WindowEvent EndedCampCard() => new()
    {
        Id = 11,
        LinkshellId = 1,
        Name = "Fafnir",
        NormalizedName = "FAFNIR",
        Status = WindowEventStatuses.Open,
        CampEndedAtUtc = Anchor,
        FirstCapturedAtUtc = Anchor,
        LastCapturedAtUtc = Anchor,
    };

    private static WindowEvent ClosedCard() => new()
    {
        Id = 12,
        LinkshellId = 1,
        Name = "Kirin",
        NormalizedName = "KIRIN",
        Status = WindowEventStatuses.Closed,
        FirstCapturedAtUtc = Anchor,
        LastCapturedAtUtc = Anchor,
    };

    private static List<string> Visible(params AttendanceSnapshot[] captures)
        => AttendanceSectionsBuilder
            .ApplyLiveCaptureFilter(captures.AsQueryable())
            .Select(s => s.Name!)
            .ToList();

    // Unfiled is the officer's to-do by definition, and nothing about it has finished.
    [Fact]
    public void AnUnfiledCapture_StaysOnThePanel()
    {
        Assert.Equal(new[] { "stayed for ToD" }, Visible(Capture("stayed for ToD")));
    }

    [Fact]
    public void ACaptureOnACardStillBeingCaptured_StaysOnThePanel()
    {
        Assert.Equal(new[] { "late arrivals" }, Visible(Capture("late arrivals", windowEvent: LiveCard())));
    }

    [Fact]
    public void ACaptureOnALiveCamp_StaysOnThePanel()
    {
        var camp = new Event { Id = 500, LinkshellId = 1, EventName = "faf d2", EndTime = null };
        Assert.Equal(new[] { "corpse run" }, Visible(Capture("corpse run", liveCamp: camp)));
    }

    // THE ONE FROM THE SCREENSHOT. End Camp hands the capture to a review card, and the panel kept
    // listing it for days afterwards.
    [Fact]
    public void ACaptureHandedToAnEndedCampsReviewCard_LeavesThePanel()
    {
        Assert.Empty(Visible(Capture("stayed for ToD", windowEvent: EndedCampCard())));
    }

    // Status=Open + CampEndedAtUtc set is EXACTLY what End Camp writes, so a rule keyed on status
    // alone would let every ended camp back onto the panel. Stated as its own case because that is
    // the mistake this is most likely to be "simplified" back into.
    [Fact]
    public void AnEndedCamp_IsNotLiveMerelyBecauseItsStatusIsStillOpen()
    {
        var card = EndedCampCard();
        Assert.Equal(WindowEventStatuses.Open, card.Status);
        Assert.Empty(Visible(Capture("stayed for ToD", windowEvent: card)));
    }

    // The other way to finish: an officer closes a "/lsm now" attendance event by hand.
    [Fact]
    public void ACaptureOnAClosedEvent_LeavesThePanel()
    {
        Assert.Empty(Visible(Capture("misc", windowEvent: ClosedCard())));
    }

    // An Event that ended without being deleted. End Event normally removes the row — and the FK
    // nulls LinkedEventId when it does, which the unfiled case above already covers — but an ended
    // camp that is still present must not read as live.
    [Fact]
    public void ACaptureOnAnEndedCamp_LeavesThePanel()
    {
        var camp = new Event { Id = 501, LinkshellId = 1, EventName = "faf d2", EndTime = Anchor };
        Assert.Empty(Visible(Capture("stayed for ToD", liveCamp: camp)));
    }

    // Tonight's work survives last night's. This is the whole point: the finished rows were
    // crowding the unfiled ones out of a capped feed.
    [Fact]
    public void FinishedCapturesLeave_WhileTonightsRemain()
    {
        var visible = Visible(
            Capture("old misc", windowEvent: EndedCampCard()),
            Capture("old stayed for ToD", windowEvent: EndedCampCard()),
            Capture("tonight, unfiled"),
            Capture("tonight, on the camp", windowEvent: LiveCard()));

        Assert.Equal(new[] { "tonight, unfiled", "tonight, on the camp" }, visible);
    }
}
