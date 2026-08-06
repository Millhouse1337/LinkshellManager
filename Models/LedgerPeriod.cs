using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// How far back a linkshell's treasury is locked. One row per linkshell, at most.
//
// ABSENCE OF A ROW MEANS NOTHING IS LOCKED. That is the same absence-is-the-default idiom as
// DkpLedgerEntry.DkpPoolId == null meaning "the default pool": it needs no backfill, and a
// linkshell that never touches this feature is never locked by it.
//
// Enforced by LedgerPeriodGuard, which is called from INSIDE TreasuryJournalWriter rather than
// from a controller, so no present or future endpoint can post around it.
public class LedgerPeriod
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // Inclusive. An entry dated on or before this instant cannot be recorded, reversed or
    // corrected. Null means unlocked, so clearing a lock does not delete the row and lose the
    // record that it was ever locked.
    public DateTime? LockedThroughUtc { get; set; }

    public DateTime? LockedAt { get; set; }

    [MaxLength(450)]
    public string? LockedByAppUserId { get; set; }

    [MaxLength(256)]
    public string? LockedByCharacterName { get; set; }

    public DateTime? UnlockedAt { get; set; }

    [MaxLength(450)]
    public string? UnlockedByAppUserId { get; set; }

    [MaxLength(256)]
    public string? UnlockedByCharacterName { get; set; }

    // Why the lock was lifted. Unlocking is the one action here that weakens the record, so it
    // says why.
    [MaxLength(512)]
    public string? UnlockReason { get; set; }
}
