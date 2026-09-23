namespace LinkshellManagerDiscordApp.ViewModels;

public sealed class LinkshellAttendanceSnapshotsViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public List<AttendanceSnapshotRow> Snapshots { get; set; } = new();
    public bool CanRename { get; set; }

    // Same officer/leader gate covers linking and quick-creating events.
    public bool CanManageEvents { get; set; }

    // Queued + live (non-ended) events for this LS. Powers the
    // "Link to event" dropdown on each unlinked snapshot card.
    public List<SelectableEventOption> SelectableEvents { get; set; } = new();
}

public sealed class SelectableEventOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsLive { get; set; }
}

public sealed class AttendanceSnapshotRow
{
    public int Id { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public string? CapturedByCharacterName { get; set; }
    public string? UtcOffset { get; set; }
    public int EntryCount { get; set; }
    public string? PrimaryZone { get; set; }
    public List<AttendanceSnapshotEntryRow> Entries { get; set; } = new();

    // Pre-rendered display strings so the view doesn't have to know how to
    // format ordinal-suffix dates or look up a zone abbreviation.
    public string CapturedAtUtcDisplay { get; set; } = string.Empty;
    public string? CapturedAtLocalDisplay { get; set; }

    // Optional user-supplied label. Set via /lsm now <name> when capturing or
    // via the inline-rename UI on the snapshots page.
    public string? Name { get; set; }

    // Optional event association. Display-only -- linking does not credit
    // attendance. Null when the snapshot stands alone.
    public int? LinkedEventId { get; set; }
    public string? LinkedEventName { get; set; }
    public bool LinkedEventIsLive { get; set; }
}


public sealed class AttendanceSnapshotEntryRow
{
    // AttendanceSnapshotEntry.Id — lets the HNM Events UI target a
    // specific person for removal. 0 when the row isn't backed by a saved
    // entry (read-only contexts that don't set it).
    public int Id { get; set; }

    public string CharacterName { get; set; } = string.Empty;
    public string? MainJob { get; set; }
    public int? MainJobLevel { get; set; }
    public string? SubJob { get; set; }
    public int? SubJobLevel { get; set; }
    public string? Zone { get; set; }

    // Typed in by an officer rather than scanned by the addon. Sorts to the bottom of the snapshot
    // and tints the row, so a hand-asserted name never reads as captured evidence.
    public bool AddedManually { get; set; }

    // What THIS capture pays them, on a row whose captures carry the money
    // (WindowEventRow.PerCaptureDkp). Null everywhere else, and on a row an officer added during
    // review — see AttendanceSnapshotEntry.DkpAmount.
    public double? DkpAmount { get; set; }

    // Whether this character is on the linkshell roster at all (RosterNameSet — an accepted invite
    // or a sign-up board self-registration, matched on the main or either alt).
    //
    // A capture reads the WHOLE alliance, so another linkshell standing at the same camp lands in
    // it. Those names were never going to be credited — every DKP path resolves a membership first
    // and silently skips one it cannot place — so listing them unmarked beside the people who ARE
    // being paid is exactly the confusion this flags.
    //
    // Computed at READ time, never stored: someone who registers mid-camp becomes creditable the
    // moment they do, and a flag stamped at capture time would go on calling them a stranger while
    // the ledger paid them. Defaults true, so a mapper with no roster in hand tags nobody.
    public bool IsRegistered { get; set; } = true;
}
