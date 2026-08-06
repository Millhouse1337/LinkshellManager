using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class WindowEvent
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    [MaxLength(128)]
    public string? Name { get; set; }

    [MaxLength(128)]
    public string? NormalizedName { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = WindowEventStatuses.Open;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime FirstCapturedAtUtc { get; set; }

    public DateTime LastCapturedAtUtc { get; set; }

    public DateTime? ClosedAtUtc { get; set; }

    // The moment window 1 opened. Pins the grid every snapshot's WindowNumber is measured against:
    // window N runs [anchor + (N-1)×cadence, anchor + N×cadence), at 10-minute steps on the
    // 7-window kings/dragons and hourly on the 25-window wyrms.
    //
    // Set ONCE at creation and never moved. FirstCapturedAtUtc is not usable for this: it slides
    // backwards whenever a post arrives carrying an older CapturedAtUtc, and a grid that shifts
    // under already-labelled snapshots would silently renumber history.
    //
    // Null on events created before this column existed — WindowGridAnchorUtc below falls back for
    // those, so their snapshots still get sensible numbers.
    public DateTime? WindowAnchorAtUtc { get; set; }

    // The anchor to actually measure windows from. A camp handed off from a Discord board uses its
    // real pop time (CampStartedAtUtc) — that's the grid the board's own window counter ran on, so
    // the two agree. A `/lsm now` camp has no pop time, and its first capture IS the start.
    [NotMapped]
    public DateTime WindowGridAnchorUtc => WindowAnchorAtUtc ?? CampStartedAtUtc ?? FirstCapturedAtUtc;

    [MaxLength(256)]
    public string? CreatedByCharacterName { get; set; }

    [MaxLength(1024)]
    public string? Notes { get; set; }

    // DKP per attending character once this event is posted to the linkshell's Google Sheet
    // AttInput tab — the baseline for anyone without a per-snapshot override. No longer entered
    // on the card (DKP is set per snapshot); saves run it through WindowEventDkp.Resolve, which
    // never lets it go back to null. See that helper for why null is dangerous.
    public double? DkpAmount { get; set; }

    // Entry Type tag the sheet's downstream formulas pivot on. Must be one of the
    // WindowEventEntryTypes constants below; auto-tagged from the monster at creation and
    // preserved by WindowEventEntryTypes.Resolve, for the same reason DkpAmount is.
    [MaxLength(32)]
    public string? EntryType { get; set; }

    // Idempotency stamp for the Post to DKP Sheet action. When set, the
    // post-to-sheet button on the card switches into a "Already posted"
    // state so officers don't accidentally double-append rows.
    public DateTime? PostedToSheetAt { get; set; }

    // First member row written to the AttInput tab during PostToSheet
    // (the header separator row sits at FirstAttInputRowNumber - 1). Set on
    // the initial append so post-post edits can rewrite J/K cells in place.
    public int? FirstAttInputRowNumber { get; set; }

    // Number of contiguous member rows written starting at
    // FirstAttInputRowNumber. Combined with the first row this lets the
    // post-post edit path target every appended data row, including the
    // non-AppUser-linked ones that have no ledger entry to consult.
    public int? AttInputRowCount { get; set; }

    // --- Camp handoff (HnmCampReviewHandoffService) ---
    //
    // Set when this review row was produced by ending an HNM camp rather than by an addon
    // "/lsm now" snapshot. Provenance only — the camp Event is RECYCLED for the next pop, not
    // deleted, so this points at a live row whose StartTime has already moved on. SET NULL on
    // delete: losing the camp must not destroy an unposted payout.
    public int? SourceEventId { get; set; }

    [ForeignKey(nameof(SourceEventId))]
    public Event? SourceEvent { get; set; }

    // The camp's own start/end, snapshotted here at End Camp because they are NOT recoverable
    // from SourceEvent later: the pop re-points Event.StartTime to the next predicted repop and
    // clears CommencementStartTime. EventHistory is written at POST time (see
    // WindowEventDkpLedgerService), so without these the history row would be dated to the next
    // pop instead of the camp that earned it.
    public DateTime? CampStartedAtUtc { get; set; }

    public DateTime? CampEndedAtUtc { get; set; }

    // The camp's event type / location, snapshotted for the same reason. EventType additionally
    // resolves the DKP pool (DkpPoolRef.Derived) at post time. WindowEvent.Name already carries
    // the camp's event name, so it isn't duplicated here.
    [MaxLength(64)]
    public string? CampEventType { get; set; }

    [MaxLength(256)]
    public string? CampEventLocation { get; set; }

    public ICollection<AttendanceSnapshot> Snapshots { get; set; } = new List<AttendanceSnapshot>();

    // Per-character DKP overrides applied at post-to-sheet time. Empty when
    // every member uses WindowEvent.DkpAmount. Populated by officers via the
    // per-row DKP input on the Window Events card.
    public ICollection<WindowEventMemberDkp> MemberDkpOverrides { get; set; } = new List<WindowEventMemberDkp>();
}

public static class WindowEventStatuses
{
    public const string Open = "Open";
    public const string Closed = "Closed";
    public const string Archived = "Archived";
}

// Valid AttInput "Entry Type" tags (column K). The downstream sheet formulas
// pivot on these exact strings, so values that don't match the set will fall
// through the formula chain silently.
// The event-wide DKP baseline. Officers set DKP per snapshot now, so the card no longer asks
// for this — but it still backs every member who has no override, so a save must never null it.
public static class WindowEventDkp
{
    // The value the card has always seeded its (now removed) input with.
    public const double Default = 1.5;

    // The amount to persist for a save that doesn't carry one: an explicitly supplied value
    // wins, else whatever the event already has, else the baseline.
    //
    // Never returns null, and that matters: WindowEventDkpLedgerService bails with 0 — no
    // exception, no log — when DkpAmount is null, so a posted event would report success and
    // credit nobody. It also feeds ApplyMemberDkpOverrides, which compares each per-character
    // value against this to decide whether to store an override row or drop it as redundant.
    public static double Resolve(double? supplied, double? stored)
        => supplied is { } s && s >= 0 ? s
            : stored is { } t && t >= 0 ? t
            : Default;
}

public static class WindowEventEntryTypes
{
    public const string KingsCamp = "Kings Camp";
    public const string WyrmsCamp = "Wyrms Camp";
    public const string MiscCamp  = "Misc Camp";
    public const string Kill      = "Kill";

    public static readonly IReadOnlyList<string> All = new[]
    {
        KingsCamp, WyrmsCamp, MiscCamp, Kill,
    };

    // Monster -> camp lookup for auto-tagging events created by name (e.g.
    // the addon's "/lsm now <monster>"). Keys are normalized the same way
    // as the lookup in FromMonsterName (whitespace-collapsed, upper case).
    // Jormungand is intentionally only in the Wyrms set: it appears on both
    // lists in FFXI lore, and Wyrms wins per the linkshell's convention.
    private static readonly HashSet<string> WyrmsMonsters = new(StringComparer.Ordinal)
    {
        "TIAMAT", "JORMUNGAND", "VRTRA",
    };

    private static readonly HashSet<string> KingsMonsters = new(StringComparer.Ordinal)
    {
        "ADAMANTOISE", "ASPIDOCHELONE", "BEHEMOTH", "FAFNIR",
        "KING BEHEMOTH", "NIDHOGG",
    };

    public static bool IsValid(string? value)
        => !string.IsNullOrEmpty(value) && All.Contains(value);

    // Picks the entry type for a freshly created window event from its
    // monster name. Wyrms is checked first so a monster that could be read
    // as either (Jormungand) lands in Wyrms Camp. Anything unrecognized —
    // including null/blank — falls back to Misc Camp.
    public static string FromMonsterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return MiscCamp;
        var key = string.Join(
            ' ',
            name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToUpperInvariant();
        if (WyrmsMonsters.Contains(key)) return WyrmsCamp;
        if (KingsMonsters.Contains(key)) return KingsCamp;
        return MiscCamp;
    }

    // The Entry Type to persist for a save that doesn't carry one.
    //
    // The field was removed from the UI — it's auto-tagged from the monster at creation, so
    // asking again was busywork. It is NOT safe to let it go null, though: WindowEventDkpLedger
    // Service gates on IsValid and returns 0 with no exception and no log, so a posted event
    // with a null tag reports "credited DKP" and credits nobody. (The sheet's column-K formulas
    // pivot on these exact strings too.) Precedence: an explicitly supplied valid value, else
    // whatever the event already carries, else re-derive from its name — which also heals rows
    // created before auto-tagging existed. Always returns a valid value, so callers need no
    // further validation.
    public static string Resolve(string? supplied, string? stored, string? eventName)
        => IsValid(supplied) ? supplied!
            : IsValid(stored) ? stored!
            : FromMonsterName(eventName);
}

public static class AttendanceSnapshotStatuses
{
    public const string Active = "Active";
    public const string PossibleDuplicate = "PossibleDuplicate";
    public const string Duplicate = "Duplicate";
    public const string Ignored = "Ignored";
}
