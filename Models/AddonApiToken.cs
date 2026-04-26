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

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public DateTime? RevokedAt { get; set; }
}
