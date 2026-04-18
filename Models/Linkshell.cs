using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class Linkshell
{
    [Key]
    public int Id { get; set; }

    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    public string? LinkshellName { get; set; }

    [NotMapped]
    public int? TotalMembers { get; set; }

    [NotMapped]
    public int? TotalItems { get; set; }

    [NotMapped]
    public int? Revenue { get; set; }

    public string? Status { get; set; }

    public string? Details { get; set; }

    public ICollection<AppUserLinkshell> AppUserLinkshells { get; set; } = new List<AppUserLinkshell>();

    public ICollection<Event> Events { get; set; } = new List<Event>();

    public ICollection<EventHistory> EventHistories { get; set; } = new List<EventHistory>();
}
