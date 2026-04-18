using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class EventHistory
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string? EventName { get; set; }

    public string? EventType { get; set; }

    public string? EventLocation { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public DateTime? CommencementStartTime { get; set; }

    public double? Duration { get; set; }

    public int? DkpPerHour { get; set; }

    public double? EventDkp { get; set; }

    public string? Details { get; set; }

    public ICollection<AppUserEventHistory> AppUserEventHistories { get; set; } = new List<AppUserEventHistory>();

    public DateTime? TimeStamp { get; set; }
}
