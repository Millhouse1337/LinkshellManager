using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AppUserLinkshell
{
    [Key]
    public int Id { get; set; }

    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string? CharacterName { get; set; }

    public string? Rank { get; set; }

    public string? Status { get; set; }

    public double? LinkshellDkp { get; set; }

    // Lifetime DKP totals seeded from the generic DKP template import. The
    // app's live totals = these seeds + the DkpLedgerEntry rows recorded AFTER
    // DkpSeedLedgerId (the ledger Id watermark at import time). This lets a
    // linkshell migrating in from an external sheet carry its lifetime
    // earned/spent without re-bookkeeping every past transaction, while
    // app-native linkshells (never seeded → all 0) compute totals purely from
    // the ledger. See Services/DkpTemplateSheetService.
    public double SeededDkpEarned { get; set; }

    public double SeededDkpSpent { get; set; }

    public int DkpSeedLedgerId { get; set; }

    public DateTime? DateJoined { get; set; }

    [Column(TypeName = "jsonb")]
    public int[]? JobLevels { get; set; }
}
