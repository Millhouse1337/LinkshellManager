using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;

namespace LinkshellManagerDiscordApp.ViewModels;

// Everything ONE expanded past-event card renders — the web twin of a card in the Activity's
// Past events panel (discord-activity/src/app/home/sidebar-panels/event-history-panel.component.ts).
//
// It exists because the card body is fetched on its own, per card, rather than rendered with the
// list: a wyrm camp archives 25 windows of roster, so building this for all ten events on a page
// of the Event System would dwarf the page itself. EventHistoryController.Card serves it; the
// standalone Details page builds the same model and spreads it over ViewBag, so the two surfaces
// cannot drift on what an officer is allowed to see or do.
public sealed class PastEventCardViewModel
{
    public required EventHistory History { get; init; }

    // Leader/officer (or an active admin override) on this event's linkshell. Gates every editor
    // in the card; a plain member gets the read-only roster and the discussion.
    public bool CanManage { get; init; }

    // The linkshell opted into member activity tracking. Only affects wording and the "counts
    // toward active" tag — the credit checkboxes themselves show regardless, because credit also
    // drives the roster's active-credit streak.
    public bool ActivityTrackingEnabled { get; init; }

    // The linkshell's DKP rounding increment (Quarter = 0.25 / Half = 0.5), used as the step on
    // every DKP input so the web can't post a value the Activity would round.
    public double DkpStep { get; init; } = 0.25;

    public List<AppUserEventHistory> Participants { get; init; } = new();

    // Roster members with no attendance row on this event. Adding one credits them and grants DKP.
    public List<AppUserLinkshell> Absentees { get; init; } = new();

    // Empty for a timed event, and for any windowed camp closed before the archive existed.
    public ArchivedWindowSet WindowArchive { get; init; } = ArchivedWindowSet.Empty;

    public List<EventComment> Comments { get; init; } = new();
    public string? CommentUserId { get; init; }

    // Where the card's forms send the browser when JavaScript is NOT driving them (the cards
    // normally submit through fetch and re-render in place). Local URLs only — validated by the
    // controller before it redirects.
    public string? ReturnUrl { get; init; }

    // What "Window 3 of N" reads against: the archive's own count where one survived, else the
    // monster's configured band.
    public int WindowDenominator => WindowArchive.WindowCount > 0
        ? WindowArchive.WindowCount
        : HnmConfig.GetWindowCount(History.EventName);

    // The Windows column only earns its place on a windowed camp. Keyed off the archive OR any
    // attendee's tally rather than off one row: a member scanned in zero windows still needs the
    // cell, and a camp whose windows were cascaded away can still have the tallies.
    public bool ShowWindows => WindowArchive.HasWindows || Participants.Any(p => p.WindowsAttended.HasValue);
}
