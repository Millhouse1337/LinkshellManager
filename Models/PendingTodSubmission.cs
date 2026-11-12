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

    [MaxLength(64)]
    public string? MonsterName { get; set; }

    public int? DayNumber { get; set; }

    public bool? Claim { get; set; }

    // Which half of a merge pair died (Nidhogg, Aspidochelone, King Behemoth): the same field as
    // Tod.Hq. Without it an approved HQ kill was saved as the NQ, so the next board's day cycle
    // never went back to Day 1.
    public bool Hq { get; set; }

    // The monster's Additional Seconds when the ToD was submitted: the same field as
    // Tod.AdditionalSeconds. Without it the approved ToD said its repop had none.
    public int AdditionalSeconds { get; set; }

    public DateTime? Time { get; set; }

    [MaxLength(32)]
    public string? Cooldown { get; set; }

    [MaxLength(32)]
    public string? Interval { get; set; }

    public DateTime? RepopTime { get; set; }

    [MaxLength(256)]
    public string? ImagePath { get; set; }

    // Which spawn window it popped on, carried through the approval queue so a member without
    // ToD-manage rights records it exactly like an officer does.
    //
    // It has to be stored HERE rather than re-derived at approval: the addon's ToD Tracker stamps
    // this at the instant it attributes the pop, and the window is a function of elapsed time, so
    // by the time an officer gets round to the queue the mob's band has cycled on and any fresh
    // reading would name a window it was never up in. Null = not recorded, same as everywhere else.
    public int? PopWindow { get; set; }
}
