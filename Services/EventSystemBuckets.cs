using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Which section of the Event System page an event belongs to.
//
// These mirror the Discord Activity's liveEvents() / queuedEvents() / isEndedCamp()
// (discord-activity/src/app/home/tabs/events-tab.component.ts) exactly, so the web page and the
// Activity tab can never disagree about where a camp sits. The Activity's DTO names map here as:
// `hnmAwaitingRepost` is HnmDefeatedAt != null (ActivityDataController.Overview.cs -> ActivityDtos),
// and `wdFinalizedAt` passes straight through.
//
// Lives in Services rather than on EventController so it can be tested without a DbContext, the
// same arrangement HnmConfig and DiscordEventMessageBuilder already use for shared rules.
public static class EventSystemBuckets
{
    public static bool IsHnmEvent(Event evt)
        => string.Equals((evt.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);

    // A camp that has popped is no longer running: a Standard defeat un-commences the board, and a
    // Manual Check In camp is stamped finalized at End Camp. Either way it waits in Pending for
    // re-post, while the roster it produced sits in the attendance cards above.
    public static bool IsEndedCamp(Event evt)
        => IsHnmEvent(evt) && (evt.HnmDefeatedAt is not null || evt.WdFinalizedAt is not null);

    public static bool IsLive(Event evt)
        => evt.CommencementStartTime.HasValue && !IsEndedCamp(evt);

    // Deliberately NOT filtered by IsEndedCamp — the Activity's queuedEvents() isn't either. Both
    // end paths null out the commencement (HnmCampPopService, HnmCampReviewHandoffService), so a
    // defeated board reaches Pending through this test alone and IsEndedCamp is belt-and-braces.
    public static bool IsPending(Event evt)
        => !evt.CommencementStartTime.HasValue;
}
