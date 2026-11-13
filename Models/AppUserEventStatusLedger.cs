using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AppUserEventStatusLedger
{
    [Key]
    public int Id { get; set; }

    public int AppUserEventId { get; set; }

    [ForeignKey(nameof(AppUserEventId))]
    public AppUserEvent? AppUserEvent { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    [MaxLength(32)]
    public string ActionType { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    public bool RequiresVerification { get; set; }

    public DateTime? VerifiedAt { get; set; }

    public DateTime? DeniedAt { get; set; }
}
