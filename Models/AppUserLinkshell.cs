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

    // Officer override of the computed active-credit streak (the roster "Count").
    // When set, it's shown instead of the attendance-computed credit streak and
    // drives Status by the linkshell's thresholds. Cleared the next time the
    // attendance status is recomputed (event close), so auto-tracking resumes.
    public int? ManualActiveCreditStreak { get; set; }

    // Officer override of the computed ABSENT streak (the roster red "Count").
    // Mutually exclusive with ManualActiveCreditStreak — setting one nulls the
    // other. When set, drives Status toward Inactive by the absence threshold.
    // Cleared on the next attendance recompute so auto-tracking resumes.
    public int? ManualAbsentStreak { get; set; }

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

    // Per-job "strong" flags for the main character, parallel to JobLevels and in
    // the same FFXI-job-id-indexed format (see ProfileJobLevels). A non-zero entry
    // marks the job as well-geared/merited so the linkshell can see at a glance who
    // brings a strong setup. Like JobLevels, the profile editor writes the same
    // value to every membership (one character, one set of strengths).
    [Column(TypeName = "jsonb")]
    public int[]? StrongJobs { get; set; }

    // Per-job free-text merit notes for the main character, catalog-aligned (index
    // 0 = WAR … 14 = SMN, NOT the FFXI-job-id format the level arrays use). Set
    // when a job is marked merited; empty otherwise. Written to every membership
    // like JobLevels/StrongJobs (one character, one set of merits).
    [Column(TypeName = "jsonb")]
    public string[]? MeritJobs { get; set; }
}
