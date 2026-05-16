using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AppUserLinkshell
{
    [Key]
    public int Id { get; set; }

    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string? CharacterName { get; set; }

    public string? Rank { get; set; }

    public string? Status { get; set; }

    public double? LinkshellDkp { get; set; }

    // Anti-abuse flag: when set and in the future, this member cannot be
    // credited an in-game loot-pool win. Set when the member undoes a
    // winning auction bid (UtcNow + Linkshell.LootBlockCooldownHours).
    // Null / past = not blocked.
    public DateTime? LootBiddingBlockedUntil { get; set; }

    public DateTime? DateJoined { get; set; }

    [Column(TypeName = "jsonb")]
    public int[]? JobLevels { get; set; }
}
