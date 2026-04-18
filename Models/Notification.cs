using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class Notification
{
    [Key]
    public int Id { get; set; }

    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    public string? NotificationType { get; set; }

    public string? CharacterNameSender { get; set; }

    public string? NotificationDetails { get; set; }

    public DateTime CreatedAt { get; set; }
}
