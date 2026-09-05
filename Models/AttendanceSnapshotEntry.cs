using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class AttendanceSnapshotEntry
{
    [Key]
    public int Id { get; set; }

    public int SnapshotId { get; set; }

    [ForeignKey(nameof(SnapshotId))]
    public AttendanceSnapshot? Snapshot { get; set; }

    [MaxLength(256)]
    public string CharacterName { get; set; } = string.Empty;

    // The account this entry belongs to, when it is already known. Addon "/lsm now" captures
    // read character names off the party list and leave this NULL — those still resolve by name
    // in WindowEventDkpLedgerService, exactly as before.
    //
    // Camp handoffs (HnmCampReviewHandoffService) DO know the account: the camp roster is keyed
    // on AppUserId. Recording it means a member whose in-game character isn't one of the four
    // names the name-resolver indexes (membership / account / two alts) still gets credited,
    // instead of being silently dropped at post time with no error.
    [MaxLength(450)]
    public string? AppUserId { get; set; }

    [MaxLength(8)]
    public string? MainJob { get; set; }

    public int? MainJobLevel { get; set; }

    [MaxLength(8)]
    public string? SubJob { get; set; }

    public int? SubJobLevel { get; set; }

    [MaxLength(128)]
    public string? Zone { get; set; }

    // True when an officer typed this person in by hand ("+ Add person") rather than the addon
    // having scanned them. Two consequences, both about not letting a hand-entered row masquerade
    // as captured evidence:
    //   * it sorts to the BOTTOM of the snapshot instead of alphabetically into the middle, so the
    //     scanned roster stays an unbroken block;
    //   * the row is tinted, so anyone reviewing the payout can see at a glance which names were
    //     asserted rather than observed.
    // False for every addon-captured entry, and for rows created before this column existed —
    // those were captured, since manual adds are recent.
    public bool AddedManually { get; set; }

    // What THIS capture pays this person, on a review row that prices captures individually
    // (WindowEvent.PerCaptureDkp). A Standard HNM camp hands off one capture per posted window, and
    // a window is priced as the open, the close, the regular rate or the kill roster — so the one
    // number a member is owed is a sum of several different amounts, and it is the windows that
    // carry them. Their payout is the sum of these across every active capture they appear in.
    //
    // NULL means "this capture prices nothing for them", and it is the state of every row written
    // before this column existed. Those review rows keep paying from WindowEventMemberDkp exactly
    // as they did, so nothing already pending re-prices itself — see WindowEvent.PerCaptureDkp,
    // which is the flag that decides which of the two a card reads.
    //
    // A person an officer adds during review lands here at null and pays 0 until they type an
    // amount. Same rule as WindowEvent.DkpAmount being seeded to 0 on a camp: an accidental add
    // must not silently pay.
    public double? DkpAmount { get; set; }
}
