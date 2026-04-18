using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AppUserEvent
{
    [Key]
    public int Id { get; set; }

    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    public string? CharacterName { get; set; }

    public string? JobName { get; set; }

    public string? SubJobName { get; set; }

    public string? JobType { get; set; }

    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public double? Duration { get; set; }

    public double? EventDkp { get; set; }

    public bool IsQuickJoin { get; set; }

    public bool? IsVerified { get; set; }

    public string? Proctor { get; set; }

    public bool? IsOnBreak { get; set; }

    public DateTime? PauseTime { get; set; }

    public DateTime? ResumeTime { get; set; }

    public ICollection<AppUserEventStatusLedger> StatusLedgerEntries { get; set; } = new List<AppUserEventStatusLedger>();
}
