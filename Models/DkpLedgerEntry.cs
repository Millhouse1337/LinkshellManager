using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class DkpLedgerEntry
{
    [Key]
    public int Id { get; set; }

    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public int? EventHistoryId { get; set; }

    [ForeignKey(nameof(EventHistoryId))]
    public EventHistory? EventHistory { get; set; }

    [MaxLength(32)]
    public string EntryType { get; set; } = string.Empty;

    public double Amount { get; set; }

    public int Sequence { get; set; }

    public DateTime OccurredAt { get; set; }

    [MaxLength(256)]
    public string? CharacterName { get; set; }

    [MaxLength(256)]
    public string? EventName { get; set; }

    [MaxLength(256)]
    public string? EventType { get; set; }

    [MaxLength(256)]
    public string? EventLocation { get; set; }

    public DateTime? EventStartTime { get; set; }

    public DateTime? EventEndTime { get; set; }

    [MaxLength(256)]
    public string? ItemName { get; set; }

    [MaxLength(1024)]
    public string? Details { get; set; }
}
