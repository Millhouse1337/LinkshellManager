using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// How far back a linkshell's treasury was locked. One row per linkshell, at most.
//
// The lock feature was removed (it never had a button in the app), so nothing reads or writes
// this table any more. The class stays only so the EF model still matches the database until
// Phase 4B drops the table.
// PHASE4B-DROP: LedgerPeriods table - treasury lock feature removed.
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
