using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One linkshell member the addon saw land an action on the mob during a
// claim-shield window. AppUserId / Matched are null/false when the addon-seen
// name didn't resolve to a current linkshell membership (kept for transparency).
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

    // WHAT they did, as a sentence: "Azurth casts Dia on the Aspidochelone."
    //
    // A name on its own is an assertion nobody can check. The addon decides a
    // tag by replaying chat (a spell has to start on the monster AND complete
    // without being interrupted), so this is the line that decision rests on --
    // which makes a wrong one visibly wrong instead of merely wrong.
    //
    // Null on rows written before this was recorded; the UI falls back to the
    // bare name.
    [MaxLength(512)]
    public string? ActionMessage { get; set; }
}
