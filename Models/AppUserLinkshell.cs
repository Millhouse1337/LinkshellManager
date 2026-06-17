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

    // For an UNSYNCED member (a placeholder AppUser, IsPlaceholder=true): the Discord
    // user id of the player it belongs to. Set when they self-register via the Discord
    // board's "you're not synced" modal, so later board signups auto-recognize them by
    // Discord id (no re-typing). Null for real, synced members. See ManualMemberService
    // + DiscordInteractionsController.ResolveSignupContextAsync.
    [MaxLength(32)]
    public string? DiscordUserId { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string? CharacterName { get; set; }

    public string? Rank { get; set; }

    public string? Status { get; set; }

    // Officer override of the computed active-credit streak (the roster "Count").
    // Acts as a SEED/baseline for the activity state machine: the machine starts
    // from this value at ManualStreakSetAt and processes counting events that
    // ended AFTER it, so a manually-set credit accumulates with subsequent
    // attendance instead of being discarded.
    public int? ManualActiveCreditStreak { get; set; }

    // Officer override of the computed ABSENT streak (the roster red "Count").
    // Mutually exclusive with ManualActiveCreditStreak — setting one nulls the
    // other. Same SEED semantics: drives the machine's starting state.
    public int? ManualAbsentStreak { get; set; }

    // When the manual streak seed above was set. The state machine only replays
    // counting events that ended after this instant (events before are superseded
    // by the manual baseline). Null when there's no manual seed.
    public DateTime? ManualStreakSetAt { get; set; }

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
