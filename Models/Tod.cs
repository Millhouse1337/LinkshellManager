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

    // Tri-state: true = Claimed (linkshell took the kill), false = Unclaimed
    // (someone else's), null = Not Specified. Null is the auto-posted state
    // for ToDs created from the addon's loot-pool flow before the user picks
    // a claim status; the addon UI keeps the Claimed/Unclaimed buttons live
    // on those rows so the linkshell can settle the status after the fact.
    public bool? Claim { get; set; }

    // Whether this pop/kill was HQ (officer-set from the Log ToD form). Shown in the
    // ToD list alongside Claim.
    public bool Hq { get; set; }

    // Extra seconds added on top of the cooldown when computing RepopTime, for fine
    // repop adjustments (the Log ToD form's "Additional seconds" input). Stored so the
    // form round-trips on edit. 0 = none.
    public int AdditionalSeconds { get; set; }

    public string? Cooldown { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? RepopTime { get; set; }

    public string? Interval { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? TimeStamp { get; set; }

    public int? TotalClaims { get; set; }

    public int? TotalTods { get; set; }

    [MaxLength(512)]
    public string? ImagePath { get; set; }

    public ICollection<TodLootDetail> TodLootDetails { get; set; } = new List<TodLootDetail>();
}
