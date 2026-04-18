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

    public DateTime? DateJoined { get; set; }
}
