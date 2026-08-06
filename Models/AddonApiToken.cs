using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AddonApiToken
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string? IssuedToAppUserId { get; set; }

    [ForeignKey(nameof(IssuedToAppUserId))]
    public AppUser? IssuedToAppUser { get; set; }

    [Required]
    [MaxLength(128)]
    public string TokenHash { get; set; } = string.Empty;

    [MaxLength(16)]
    public string TokenPrefix { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? Label { get; set; }

    // Groups every token minted from a single pairing code. One code now pairs
    // all of a user's linkshells, so revoking has to unhook the whole set --
    // otherwise "Revoke" on the one row the page shows would leave the addon
    // still talking to the other linkshells, which reads as revoke not working.
    // Null on tokens issued before this existed; those revoke individually,
    // exactly as they did when each was paired on its own.
    public Guid? PairingBatchId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public DateTime? RevokedAt { get; set; }
}
