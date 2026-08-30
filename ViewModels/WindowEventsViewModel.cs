namespace LinkshellManagerDiscordApp.ViewModels;

// Per-character DKP override posted with the Save / Post / Update-on-Sheet
// forms. The view emits one hidden CharacterName + one number DkpAmount field
// per row in the combined-members table.
public sealed class WindowEventMemberDkpInput
{
    public string? CharacterName { get; set; }
    public double? DkpAmount { get; set; }
}

public sealed class WindowEventsViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public bool CanManage { get; set; }
    public List<WindowEventRow> OpenEvents { get; set; } = new();
    public List<WindowEventRow> ClosedEvents { get; set; } = new();
    public List<WindowSnapshotRow> UnlinkedSnapshots { get; set; } = new();

    // How many unlinked captures exist in total, and how many this list is allowed to show.
    // Total > cap means the section is hiding some, and it says so: with every /lsm now landing
    // here, an officer who never triages would otherwise just stop seeing the oldest ones.
    public int UnlinkedTotalCount { get; set; }
    public int UnlinkedDisplayCap { get; set; }

    // Every character name in this linkshell, used to populate the
    // "Add a character by name…" typeahead (a shared <datalist>) on the
    // snapshot roster editor.
    public List<string> RosterCharacterNames { get; set; } = new();
}

// Backs the searchable "Attendance History" page (closed Window Events only).
public sealed class WindowEventsHistoryViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public bool CanManage { get; set; }
    public string? Query { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalCount { get; set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public List<WindowEventRow> Events { get; set; } = new();
}

public sealed class WindowEventRow
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime FirstCapturedAtUtc { get; set; }
    public DateTime LastCapturedAtUtc { get; set; }
    public string FirstCapturedDisplay { get; set; } = string.Empty;
    public string LastCapturedDisplay { get; set; } = string.Empty;
    public string? CreatedByCharacterName { get; set; }
    public int SnapshotCount { get; set; }
    public int ActiveSnapshotCount { get; set; }
    public int IgnoredSnapshotCount { get; set; }

    // Captures still waiting for an officer's Confirm. Non-zero blocks Post to DKP sheet: those
    // members are missing from the combined roster below, so paying out now would quietly short
    // them.
    public int PendingSnapshotCount { get; set; }

    // The alliances that contributed to the combined roster, ascending. More than one is the
    // normal shape for a big camp — it means each alliance fielded its own poster.
    public List<int> AllianceNumbers { get; set; } = new();

    public int CombinedMemberCount { get; set; }
    public double? DkpAmount { get; set; }
    public string? EntryType { get; set; }
    public DateTime? PostedToSheetAt { get; set; }
    public string? PostedToSheetDisplay { get; set; }
    public List<WindowSnapshotRow> Snapshots { get; set; } = new();

    // The same captures, split by slot. Two lists rather than one filtered twice in the view,
    // because both surfaces render them as separate sections and the split rule (what counts as
    // Misc) belongs in one place.
    public List<WindowSnapshotRow> WindowSnapshots { get; set; } = new();
    public List<WindowSnapshotRow> MiscSnapshots { get; set; } = new();
    public int MiscSnapshotCount { get; set; }

    // Per-member DKP for anyone credited ONLY by Misc posts. Null means they are paid the same as
    // a window attendee, which is the default.
    public double? MiscDkpAmount { get; set; }

    // This camp's own window grid, for the slot picker. HasWindowGrid false means there are no
    // numbers to offer (Sky gods, farm NMs) — Misc is still selectable, the number is not.
    public int WindowCount { get; set; }
    public bool HasWindowGrid { get; set; }
    public List<WindowCombinedMemberRow> CombinedMembers { get; set; } = new();
}

public sealed class WindowSnapshotRow
{
    public int Id { get; set; }
    public int? WindowEventId { get; set; }
    public string? Name { get; set; }
    public string SnapshotStatus { get; set; } = string.Empty;
    public DateTime CapturedAtUtc { get; set; }
    public string CapturedAtDisplay { get; set; } = string.Empty;
    public string? CapturedByCharacterName { get; set; }
    public string? PrimaryZone { get; set; }
    public int EntryCount { get; set; }

    // Which alliance this capture came from, and its ready-to-render label. Null on rows captured
    // before per-alliance posting existed — those render as "Unassigned" rather than being
    // silently presented as alliance 1.
    public int? AllianceNumber { get; set; }
    public string AllianceLabel { get; set; } = string.Empty;

    // Posted by a member without moderation rights and not yet confirmed. Excluded from the
    // combined roster and from DKP until an officer acts on it.
    public bool IsPending { get; set; }

    public string? VerifiedAtDisplay { get; set; }

    // The spawn window this capture was taken in, off the event's fixed grid. Null on camps with no
    // cadence (Sky gods, farm NMs, ad-hoc posts) — the UI then shows no window tag at all.
    public int? WindowNumber { get; set; }

    // Ready-to-render label: "Window 3 of 25", or plain "Window 3" if the total isn't known.
    // Null whenever WindowNumber is.
    public string? WindowLabel { get; set; }

    // Window or Misc. Distinct from WindowNumber being null, which means "this camp runs no
    // window grid" — an ungridded camp still files ordinary Window captures.
    public string SlotKind { get; set; } = string.Empty;
    public bool IsMisc { get; set; }

    // What the chip reads: "Misc", or the WindowLabel. Null when there is nothing to show.
    public string? SlotLabel { get; set; }

    public List<AttendanceSnapshotEntryRow> Entries { get; set; } = new();
}

public sealed class WindowCombinedMemberRow
{
    public string CharacterName { get; set; } = string.Empty;
    public string? MainJob { get; set; }
    public int? MainJobLevel { get; set; }
    public string? SubJob { get; set; }
    public int? SubJobLevel { get; set; }
    public string? Zone { get; set; }
    public int SnapshotCount { get; set; }

    // Which alliances this character was captured in, ascending. Usually one; two means they
    // moved between alliances mid-camp, which is worth seeing rather than flattening away.
    public List<int> AllianceNumbers { get; set; } = new();

    // Per-character DKP override if one is set for this Window Event;
    // null means the event's default DkpAmount applies.
    public double? DkpAmountOverride { get; set; }

    // The effective DKP for this character: override if set, otherwise the
    // event's default. The view uses this to seed the per-row DKP input.
    public double? EffectiveDkpAmount { get; set; }

    // Where this member's credit came from: "Window", "Misc", or "Both". It is what tells an
    // officer why one person on the roster is priced differently from the person above them.
    public string CreditSource { get; set; } = string.Empty;
}
