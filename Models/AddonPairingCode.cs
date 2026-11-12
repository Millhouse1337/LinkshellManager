using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AddonPairingCode
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(16)]
    public string Code { get; set; } = string.Empty;

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string? IssuedToAppUserId { get; set; }

    [ForeignKey(nameof(IssuedToAppUserId))]
    public AppUser? IssuedToAppUser { get; set; }

    // PHASE4B-DROP: AddonPairingCodes.Label - always null (nothing sent one); no longer written or read.
    [MaxLength(128)]
    public string? Label { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? ConsumedAt { get; set; }

    // PHASE4B-DROP: AddonPairingCodes.ConsumedTokenId - written on redeem but never read; used codes
    // are deleted the next time a code is made anyway.
    public int? ConsumedTokenId { get; set; }
}
