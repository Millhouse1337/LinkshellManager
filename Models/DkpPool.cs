using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// A per-linkshell DKP wallet. Event types are PARTITIONED across a linkshell's pools:
// an event type's DKP is earned into its pool, and loot from that event type is paid
// out of the same pool. Exactly one pool per linkshell is IsDefault — the catch-all for
// every event type not explicitly mapped (including null / custom / "Other") and the home
// of adjustments and imports.
//
// Pool BALANCES are not stored: they're derived from the DkpLedgerEntry rows (see
// DkpPoolBalanceService). A fresh linkshell has exactly one pool and no mappings, which
// makes the whole feature a no-op until an officer creates a second one.
public class DkpPool
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    // Exactly one per linkshell (enforced by a partial unique index).
    public bool IsDefault { get; set; }

    // A colour KEY from DkpPoolAccents (e.g. "Blue"), never a hex — see STYLE.md. Each key maps
    // to a design token in the stylesheets; the swatch shown next to the group's colour picker.
    [MaxLength(16)]
    public string? Accent { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<DkpPoolEventType> EventTypes { get; set; } = new List<DkpPoolEventType>();
}

// The colour vocabulary for a DKP group's tag/swatch. Plain colour names (NOT the EventBoardThemes
// theme keys) — each maps to a design token in the stylesheets (web lsm-theme.css --*-main,
// Activity _tokens.scss). Resolve() normalises anything else — including legacy theme keys still
// stored on old rows — to Default, so the swatch always lands on a known colour.
public static class DkpPoolAccents
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "Blue", "Green", "Red", "Orange", "Gold", "Purple", "Cyan", "Gray"
    };

    public const string Default = "Blue";

    public static string Resolve(string? value)
    {
        var match = All.FirstOrDefault(a => string.Equals(a, value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? Default;
    }
}
