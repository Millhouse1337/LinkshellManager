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

    // The grid this camp's snapshots are numbered against, captured at creation from the
    // linkshell's monster setup. Same set-once-never-moved contract as WindowAnchorAtUtc above, and
    // for the same reason: AttendanceSectionsBuilder DERIVES a missing window number at read time,
    // so a camp that re-read the live config would silently renumber history the first time someone
    // edited the monster's cadence.
    //
    // Null on rows created before this column existed, and on any monster with no grid — the
    // HnmConfig fallback then answers, exactly as it always did.
    public int? WindowCount { get; set; }
    public int? WindowMinutes { get; set; }

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

    // DKP for members credited ONLY from Misc posts — people at the camp but outside any window.
    //
    // NULL is MEANINGFUL here, unlike DkpAmount: it means "pay them exactly what a window attendee
    // gets", which is the default. WindowEventDkp.Resolve deliberately never yields null for
    // DkpAmount because the ledger silently credits nobody without it; this column has no such
    // hazard, because null simply falls through to DkpAmount.
    public double? MiscDkpAmount { get; set; }

    // This review row prices each CAPTURE, not each member: a person's payout is the sum of
    // AttendanceSnapshotEntry.DkpAmount across every active capture they appear in, and
    // WindowEventMemberDkp is not consulted at all.
    //
    // Set by the camp handoff for a STANDARD HNM camp, and only there. That is the one shape where
    // the money genuinely varies per capture — one capture per posted window, each priced as the
    // open, the close, the regular rate or the kill roster (HnmStandardCampFinalizer.WindowValue) —
    // so a single per-member total could only ever show the same number in every window, which is
    // what it did. Manual Check In camps are excluded deliberately: their credit comes from the
    // check-in RANGE, so a member is paid for windows that have no capture at all, and per-capture
    // amounts would have nothing truthful to say.
    //
    // FALSE on every row written before this existed, which is what keeps in-flight camps paying
    // from their per-member overrides exactly as they were reviewed.
    public bool PerCaptureDkp { get; set; }

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

    // The Past Event this camp was archived as, written at END CAMP.
    //
    // It used to be written at POST, which meant a camp that ended and was then RECYCLED for the
    // next pop left no trace anywhere until an officer got round to reviewing its payout: gone
    // from the live list (the board is reused for the next pop), absent from Past Events, and
    // recorded only as a pending review row. Ending a camp is the thing that makes it past, so
    // the archive is written then, and this points at it.
    //
    // Post still owns the DKP -- it reconciles this history's roster and amounts to whatever the
    // review settled on. Null on review rows staged before this column existed, and on ordinary
    // "/lsm now" snapshot rows, which are not camps and get no history at all.
    //
    // SET NULL on delete, for the same reason SourceEventId is: deleting the archive must not
    // destroy an unposted payout.
    public int? CampEventHistoryId { get; set; }

    [ForeignKey(nameof(CampEventHistoryId))]
    public EventHistory? CampEventHistory { get; set; }

    // The camp's own start/end, snapshotted here at End Camp because they are NOT recoverable
    // from SourceEvent later: the pop re-points Event.StartTime to the next predicted repop and
    // clears CommencementStartTime. The archive above is dated from these rather than from
    // SourceEvent, which by post time points at a pop that has not happened yet.
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

    // The Misc rate to apply for a save that doesn't carry one. Same precedence shape as Resolve,
    // but the floor is the event's own resolved default rather than the constant: "misc pays the
    // same as a window" is the documented default, so an event that never sets a misc rate must
    // price misc-only members identically to everyone else.
    public static double ResolveMisc(double? supplied, double? stored, double resolvedDefault)
        => supplied is { } s && s >= 0 ? s
            : stored is { } t && t >= 0 ? t
            : resolvedDefault;
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
    public const string Ignored = "Ignored";

    // (PossibleDuplicate / Duplicate lived here, along with a whole flagging pass that compared
    // rosters and marked a >=75%-overlapping capture for officer review.
    //
    // The alliance number retired it. A duplicate was only ever ambiguous because the server had
    // no way to tell "the same alliance captured twice" from "two alliances captured at once" --
    // it had to guess from name overlap, and it guessed wrong in both directions. Now the two
    // cases are distinguishable outright: same alliance inside the merge window UNIONS into one
    // snapshot (adding people the earlier post missed, never removing any), and different
    // alliances stay separate rows by construction. There is nothing left for an officer to
    // adjudicate, and a flagged snapshot was silently EXCLUDED from the combined roster -- so the
    // feature's failure mode was under-paying people who were genuinely there.)

    // Posted by someone without live-event moderation rights. Anyone paired to the linkshell may
    // post a capture now, so a roster can arrive before anyone has vouched for it — Pending holds
    // it visible but inert until an officer Confirms.
    //
    // Almost nothing had to learn about this status to make it safe: BuildCombinedMembers, the DKP
    // ledger and the merge-target search all read ACTIVE snapshots only, so a Pending row is
    // excluded from the combined roster and from the payout for free. Reject reuses Ignored, which
    // those same queries already filter out.
    public const string Pending = "Pending";
}

public static class AttendanceSnapshotAlliances
{
    // Upper bound on AttendanceSnapshot.AllianceNumber. An FFXI alliance is 18 people, so six of
    // them is 108 — past any turnout this app has to render, and low enough that a malformed
    // client cannot invent alliance 40,000.
    public const int MaxAllianceNumber = 6;

    // The number to store for a post that carries none: an explicit choice wins (clamped),
    // otherwise alliance 1. Defaulting rather than storing null matters because null already means
    // something else on this column — "captured before per-alliance posting existed".
    public static int Resolve(int? supplied)
        => supplied is { } value ? Math.Clamp(value, 1, MaxAllianceNumber) : 1;

    // Null is rendered rather than hidden: a legacy snapshot sitting beside labelled ones should
    // say why it has no alliance, not look like alliance 1.
    public static string Label(int? allianceNumber)
        => allianceNumber is { } value ? $"Alliance {value}" : "Unassigned";

    // The human label for an alliance, preferring WHO it is over WHICH NUMBER it got.
    //
    // A number is an ordinal an officer has to decode ("which one was 2 again?"); a name is the
    // answer they actually wanted. The leader wins when the game confirmed one, then whoever the
    // addon recognised the alliance by, and the bare number is the fallback for legacy rows.
    public static string Label(int? allianceNumber, string? allianceKey, string? leaderName = null)
    {
        var named = !string.IsNullOrWhiteSpace(leaderName) ? leaderName!.Trim()
            : !string.IsNullOrWhiteSpace(allianceKey) ? allianceKey!.Trim()
            : null;
        return named is null ? Label(allianceNumber) : $"{named}'s alliance";
    }
}

// Whether a snapshot was filed against a numbered window or as a miscellaneous post.
//
// Lives beside AttendanceSnapshotStatuses/Alliances because it is the third axis an officer sorts
// a capture on, and all three are read together by every mapper.
public static class AttendanceSnapshotSlotKinds
{
    // Filed against a numbered window — or against an ungridded camp, where there is no number to
    // give but the capture is still an ordinary one.
    public const string Window = "Window";

    // At the camp, outside any window. Always carries a null WindowNumber.
    public const string Misc = "Misc";

    public static readonly IReadOnlyList<string> All = new[] { Window, Misc };

    public static bool IsMisc(string? value) => string.Equals(value, Misc, StringComparison.Ordinal);

    // Fails CLOSED to Window on anything unrecognised. Window is the safe default in every sense:
    // it is what every pre-existing row means, and it prices a member at the ordinary rate rather
    // than at a misc rate an officer never chose.
    public static string Resolve(string? value) => IsMisc(value) ? Misc : Window;
}
