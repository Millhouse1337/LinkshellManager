using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One claim-shield lottery window captured by the lsm addon: from a spawn
// announcement until the lottery resolves. Records the monster, whether the
// linkshell won, the total players in the lottery, and which linkshell
// members the addon saw acting during the window. No DKP / repop side effect
// -- purely an audit record (mirrors AttendanceSnapshot's shape).
public class ClaimShieldCapture
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // The camp this lottery belonged to, so the capture can be shown alongside
    // that event's attendance windows instead of only on the linkshell-wide ToD
    // page. Set from the addon's linked event when there is one, otherwise
    // matched server-side by monster + time (see ResolveClaimShieldEventAsync).
    //
    // Nullable on purpose: a pop can be captured with no camp open at all, and
    // that record is still worth keeping. SetNull on delete -- losing the event
    // must not delete the audit row.
    public int? EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    [MaxLength(128)]
    public string MonsterName { get; set; } = string.Empty;

    public bool Won { get; set; }

    public int TotalPlayers { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    [MaxLength(256)]
    public string? CapturedByCharacterName { get; set; }

    // The result chat line, scrubbed (audit / debugging aid).
    [MaxLength(512)]
    public string? CapturedMessage { get; set; }

    // Total member rows stored (matched + unmatched).
    public int MemberCount { get; set; }

    // Subset of MemberCount that resolved to a current linkshell member.
    public int MatchedCount { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ICollection<ClaimShieldCaptureMember> Members { get; set; } = new List<ClaimShieldCaptureMember>();
}
