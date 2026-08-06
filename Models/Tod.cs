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

    // Which pop window the monster showed up on, as recorded on the Log ToD / End Camp forms.
    // Informational history on a standalone ToD; on an HNM camp the same number is ALSO stamped
    // on Event.WdPopWindow, which is what actually caps attendance credit. null = not recorded.
    public int? PopWindow { get; set; }

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

    // Tri-state: true = this linkshell got the kill, false = it didn't, null = not recorded.
    // Set from the End Camp / Post ToD form alongside Claim, and it drives the kill bonus at
    // finalize in BOTH attendance modes (Standard via HnmStandardCampFinalizer, Manual Check In
    // via Event.WdKilled → WdCampFinalizer).
    //
    // Lives on Tod rather than Event because an HNM board is RECYCLED for the next camp — an
    // Event column would be overwritten by the following pop and answer nothing historically,
    // whereas the Tod is the durable per-pop record (same reasoning as Claim / PopWindow above).
    // Before this existed the kill outcome survived only as free text inside a DKP ledger note.
    public bool? Killed { get; set; }

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
