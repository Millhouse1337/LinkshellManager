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

    public ICollection<AttendanceSnapshot> Snapshots { get; set; } = new List<AttendanceSnapshot>();
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

    public static bool IsValid(string? value)
        => !string.IsNullOrEmpty(value) && All.Contains(value);
}

public static class AttendanceSnapshotStatuses
{
    public const string Active = "Active";
    public const string PossibleDuplicate = "PossibleDuplicate";
    public const string Duplicate = "Duplicate";
    public const string Ignored = "Ignored";
}
