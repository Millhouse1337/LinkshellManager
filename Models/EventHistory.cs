using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class EventHistory
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string? EventName { get; set; }

    public string? EventType { get; set; }

    public string? EventLocation { get; set; }

    // The party setup TEMPLATE this event ran with, recorded at close so the next pop of the same
    // camp can inherit it. Always a template (PartySetup.OwnerEventId == null), never a per-event
    // snapshot: a snapshot is cascade-deleted with the event, so storing one here would leave a
    // dangling reference the moment the row it describes went away.
    //
    // This is the ONLY place the choice survives an ended camp. Ending an event deletes the Event
    // row outright, taking PartySetupId with it — which is why a camp ended from the addon and
    // re-created from its ToD used to come back with no board attached.
    public int? PartySetupId { get; set; }

    [ForeignKey(nameof(PartySetupId))]
    public PartySetup? PartySetup { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public DateTime? CommencementStartTime { get; set; }

    public double? Duration { get; set; }

    public int? DkpPerHour { get; set; }

    public double? EventDkp { get; set; }

    public string? Details { get; set; }

    // Copied from Event.CountsTowardActive at close (the live Event is deleted, so
    // the activity calculation reads this permanent record). When true, this event
    // is part of each attendee's attendance/absence streak.
    public bool CountsTowardActive { get; set; } = true;

    public ICollection<AppUserEventHistory> AppUserEventHistories { get; set; } = new List<AppUserEventHistory>();

    // The camp's posted attendance windows, re-parented here at close instead of being deleted
    // with the Event. Empty for every non-windowed event, and for any HNM closed before the
    // archive existed — that data was cascaded away and cannot be recovered.
    public ICollection<EventAttendanceWindow> AttendanceWindows { get; set; } = new List<EventAttendanceWindow>();

    // The spawn window the monster popped on, and how many windows that camp cycled ("4 of 7").
    //
    // Stamped at End Camp by HnmCampReviewHandoffService, because nothing else keeps it: the board
    // is recycled for the next pop, and the Tod only carries it when the board's own End Camp form
    // wrote it there -- the addon's End Event never does. It is the value that camp PRICED on (the
    // close is resolved against it), so Past Events cannot show a different pop window than the
    // one the money was computed from.
    //
    // Null on a timed event, and on every camp archived before this existed.
    public int? PopWindow { get; set; }

    public int? PopWindowCount { get; set; }

    public DateTime? TimeStamp { get; set; }
}
