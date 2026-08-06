namespace LinkshellManagerDiscordApp.ViewModels;

// Backs the combined Event System page (/Event). Three sections, in the same order the Discord
// Activity's Events tab uses (discord-activity/src/app/home/tabs/events-tab.component.html):
//
//   Current Field Activity — live camps, then the OPEN attendance events they produce
//   Pending Events         — queued camps (not commenced)
//   attendance archive     — unlinked snapshots + CLOSED attendance events (triage, not the board)
//   Past events            — closed TIMED events (EventHistory)
//
// The page used to be a single flat list of every event with the attendance sections stacked on
// top; splitting it here rather than in the view keeps the bucketing predicates next to the
// entities they read (see EventController.Lifecycle.IsLiveEvent / IsPendingEvent).
public sealed class EventSystemPageViewModel
{
    public int? LinkshellId { get; set; }
    public string? LinkshellName { get; set; }

    // --- Section 1: Current Field Activity ---
    public List<EventViewModel> LiveEvents { get; set; } = new();

    // --- Section 2: Pending Events ---
    public List<EventViewModel> PendingEvents { get; set; } = new();

    // Open attendance events + unlinked snapshots + the roster typeahead source. Null when there
    // is no active linkshell, the linkshell is Sky/Sea/Dynamis, or the viewer has no membership —
    // attendance then renders NOWHERE on this page.
    //
    // The SAME instance is handed to both the 'live' and the 'archive' render of
    // _AttendanceSections: the archive's "Attach existing" control reads OpenEvents, so do not
    // blank it out for that call.
    public WindowEventsViewModel? Attendance { get; set; }

    // --- Section 3a: attendance archive (CLOSED Window Events), server-paged + searchable ---
    // Null exactly when Attendance is null.
    public WindowEventsHistoryViewModel? ClosedAttendance { get; set; }

    // --- Section 3b: Past Events (closed TIMED events) ---
    public EventHistoryListViewModel PastEvents { get; set; } = new();

    // Viewer context. These were six ViewBag keys read only by Views/Event/Index.cshtml; the page
    // now hands them to _TimedEventCard through ViewData, so they need a typed home first.
    public string? CurrentCharacterName { get; set; }
    public string? CurrentAppUserId { get; set; }
    public List<string> SignupCharacters { get; set; } = new();
    public List<string> SignUpRoleOptions { get; set; } = new();
    public List<string> SignUpMainJobOptions { get; set; } = new();
    public List<string> SignUpSubJobOptions { get; set; } = new();

    public int LiveCount => LiveEvents.Count;
    public int PendingCount => PendingEvents.Count;
    public int LiveAttendanceCount => Attendance?.OpenEvents.Count ?? 0;

    // Mirrors the Activity's showNoLiveActivity(): the "nothing running" card must never sit ABOVE
    // a list of attendance cards, which is the common case between pops. The Activity also has a
    // busy() branch to stop the card flashing before its fetch settles; server-rendered Razor has
    // nothing in flight, so there's no analogue here.
    public bool ShowNoLiveActivity => LiveEvents.Count == 0 && LiveAttendanceCount == 0;
}

// Paged/searchable Past Events list. Deliberately shaped like WindowEventsHistoryViewModel so the
// two archive blocks on this page page and search the same way.
public sealed class EventHistoryListViewModel
{
    public string? Query { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    // Rows matching Query.
    public int TotalCount { get; set; }

    // Rows before Query was applied — feeds the Activity's "{filtered} of {total}" tally.
    public int UnfilteredCount { get; set; }

    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public List<Models.EventHistory> Items { get; set; } = new();
}
