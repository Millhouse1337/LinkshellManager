using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class Invite
{
    [Key]
    public int Id { get; set; }

    public string AppUserId { get; set; } = string.Empty;

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string Status { get; set; } = "Pending";
}
