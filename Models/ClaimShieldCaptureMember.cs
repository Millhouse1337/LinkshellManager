using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One linkshell member the addon saw acting on the mob during a claim-shield
// window. AppUserId / Matched are null/false when the addon-seen name didn't
// resolve to a current linkshell membership (kept for transparency).
public class ClaimShieldCaptureMember
{
    [Key]
    public int Id { get; set; }

    public int CaptureId { get; set; }

    [ForeignKey(nameof(CaptureId))]
    public ClaimShieldCapture? Capture { get; set; }

    [MaxLength(256)]
    public string CharacterName { get; set; } = string.Empty;

    [MaxLength(450)]
    public string? AppUserId { get; set; }

    public bool Matched { get; set; }
}
