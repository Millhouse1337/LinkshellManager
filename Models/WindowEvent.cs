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

    [MaxLength(256)]
    public string? CreatedByCharacterName { get; set; }

    [MaxLength(1024)]
    public string? Notes { get; set; }

    // DKP per attending character once this event is posted to the linkshell's
    // Google Sheet AttInput tab. Null until the officer fills it in on the
    // Window Events card — the Post to DKP Sheet button is disabled while
    // this is null because the sheet would otherwise receive zeros.
    public double? DkpAmount { get; set; }

    // Entry Type tag the sheet's downstream formulas pivot on. Must be one
    // of the WindowEventEntryTypes constants below; null is rejected by the
    // Post to DKP Sheet endpoint for the same reason DkpAmount is.
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
}

public static class AttendanceSnapshotStatuses
{
    public const string Active = "Active";
    public const string PossibleDuplicate = "PossibleDuplicate";
    public const string Duplicate = "Duplicate";
    public const string Ignored = "Ignored";
}
