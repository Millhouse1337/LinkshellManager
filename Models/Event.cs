using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class Event
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string? EventName { get; set; }

    public string? EventType { get; set; }

    public string? EventLocation { get; set; }

    public string? CreatorUserId { get; set; }

    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public DateTime? CommencementStartTime { get; set; }

    public double? Duration { get; set; }

    public int? DkpPerHour { get; set; }

    public double? EventDkp { get; set; }

    public string? Details { get; set; }

    public ICollection<Job> Jobs { get; set; } = new List<Job>();

    public ICollection<AppUserEvent> AppUserEvents { get; set; } = new List<AppUserEvent>();

    public ICollection<AppUserEventStatusLedger> StatusLedgerEntries { get; set; } = new List<AppUserEventStatusLedger>();

    public ICollection<EventLootDetail> EventLootDetails { get; set; } = new List<EventLootDetail>();

    public DateTime? TimeStamp { get; set; }
}
