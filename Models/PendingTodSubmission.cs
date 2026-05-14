using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class PendingTodSubmission
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string? SubmittedByAppUserId { get; set; }

    [ForeignKey(nameof(SubmittedByAppUserId))]
    public AppUser? SubmittedBy { get; set; }

    public DateTime SubmittedAtUtc { get; set; }

    [MaxLength(512)]
    public string? ReviewNotes { get; set; }

    [MaxLength(64)]
    public string? MonsterName { get; set; }

    public int? DayNumber { get; set; }

    public bool? Claim { get; set; }

    public DateTime? Time { get; set; }

    [MaxLength(32)]
    public string? Cooldown { get; set; }

    [MaxLength(32)]
    public string? Interval { get; set; }

    public DateTime? RepopTime { get; set; }

    [MaxLength(256)]
    public string? ImagePath { get; set; }

    public ICollection<PendingTodLootSubmission> LootRows { get; set; } = new List<PendingTodLootSubmission>();
}
