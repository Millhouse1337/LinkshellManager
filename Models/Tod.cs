using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class Tod
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string? MonsterName { get; set; }

    public int? DayNumber { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? Time { get; set; }

    public bool Claim { get; set; }

    public string? Cooldown { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? RepopTime { get; set; }

    public string? Interval { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? TimeStamp { get; set; }

    public int? TotalClaims { get; set; }

    public int? TotalTods { get; set; }

    public ICollection<TodLootDetail> TodLootDetails { get; set; } = new List<TodLootDetail>();
}
