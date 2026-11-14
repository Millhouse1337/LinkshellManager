using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// A standing "re-post the HNM signup board" template, one per spawn (LinkshellId,
// MonsterName). Switched on and off only by the "Re-post board" choice on the End Camp /
// Post ToD form (HnmRecurringBoardService.ApplyEndCampChoiceAsync). After a camp ends its
// board is parked, not deleted; when a NEW ToD for the monster is recorded,
// HnmRecurringBoardBackgroundService brings that parked board back (or creates a new one
// if there is none) LeadHours before the predicted next pop (Tod.RepopTime - LeadHours).
public class HnmRecurringBoard
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // Canonical monster name (matches Tod.MonsterName / Event.AssignedMonsterName).
    [MaxLength(256)]
    public string MonsterName { get; set; } = string.Empty;

    // Turning "Re-post board" off on the End Camp / Post ToD form (or removing the board) sets
    // this false rather than deleting the row, so the last-seen ToD stamp is preserved.
    public bool Enabled { get; set; } = true;

    // How long BEFORE the predicted pop (Tod.RepopTime) the board re-posts, in hours.
    // Fractional so the lead can be entered as hours/minutes/seconds (e.g. 1.5 = 1h30m).
    public double LeadHours { get; set; } = 1;

    // The board template carried onto each recreated event (all optional).
    public int? PartySetupId { get; set; }

    [ForeignKey(nameof(PartySetupId))]
    public PartySetup? PartySetup { get; set; }

    [MaxLength(2048)]
    public string? Details { get; set; }

    [MaxLength(256)]
    public string? EventLocation { get; set; }

    [MaxLength(256)]
    public string? EventNameTemplate { get; set; }

    // The id of the most recent Tod already acted on, so a genuinely-new ToD is
    // required to trigger the next recreation (no duplicate boards for one ToD).
    public int? LastSourceTodId { get; set; }

    public string? CreatedByAppUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
