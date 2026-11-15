using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

/// <summary>
/// One member has one key item on one board.
///
/// PRESENCE IS THE FACT - there is no HasIt flag, and unticking a cell DELETES the row.
///
/// That is what makes "who still needs it" a set difference against the CURRENT roster, which is
/// exactly what the card drawer renders. It also means a member who joins next month starts
/// correctly at "needs it" with no backfill, and it removes the tri-state a nullable flag would
/// create, where a row saying false and no row at all are two ways to say the same thing that two
/// surfaces could word differently. No row at all is the normal state, not a missing one.
/// </summary>
public class ChartMemberKeyItem
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    /// <summary>Board key from <see cref="Services.ChartBoardCatalog"/> - "Dynamis" today.</summary>
    [MaxLength(16)]
    public string Board { get; set; } = string.Empty;

    /// <summary>
    /// Catalog spelling, stored verbatim like ChartPopItem.ItemName and ChartPopItem.Boss - so
    /// renaming one in the catalog orphans every row spelled the old way and needs a data migration.
    /// Unlike an item name this one is validated against a CLOSED list on the way in
    /// (ChartBoardCatalog.NormalizeKeyItemName), because the grid draws one column per catalog entry
    /// and a name it does not have would land in no column at all.
    /// </summary>
    [MaxLength(128)]
    public string KeyItemName { get; set; } = string.Empty;

    // The membership the tick was made on. Plain int with no relationship. This build reads ticks by
    // AppUserId below and only writes this for older builds: the build before RosterSwitch reads the
    // grid by it, and its model has it as a plain int, so a NULL here would break its key-item grid
    // after a rollback. Nullable since AddCharacters so the next deploy can stop writing it (a
    // rollback from that deploy lands on this build, which reads NULL fine); the deploy after drops it.
    public int? MembershipId { get; set; }

    // WHOSE tick it is: the member's account (AccountJobsAndKeyItemAccounts, RosterSwitch). The grid
    // matches ticks to the roster by this, because membership ids are not stable - a membership is
    // re-created on unsync/resync - while the account stays, as wishlist requests already use.
    // Required, with a foreign key to the account (a deleted account takes its ticks with it) and
    // one tick per account per key item. Every save that adds a row or changes its membership stamps
    // it from the membership (ApplicationDbContext.StampKeyItemAccounts), and a placeholder claim
    // moves it (UserMergeService).
    //
    // Removing somebody from the roster leaves their rows behind. They never render - BuildGrid is
    // roster-driven - and the same person coming back gets what they had, even on a new membership.
    // Typed string? only because the save hook fills it; the database never holds NULL here.
    [MaxLength(450)]
    public string? AppUserId { get; set; }

    // WHO ticked it. This is the whole audit the feature needs: a member ticking their own and an
    // officer ticking theirs write an identical row, and this is the only thing that tells them
    // apart afterwards.
    [MaxLength(450)]
    public string? SetByAppUserId { get; set; }

    [MaxLength(256)]
    public string? SetByCharacterName { get; set; }

    public DateTime SetAt { get; set; } = DateTime.UtcNow;

    // No Boss column. Which card a key item belongs to is a CATALOG fact, resolved through
    // ChartBoard.KeyItemFor - storing it would make a second copy that can disagree with the
    // catalog, and it would need a special case for the one key item that belongs to no card at all.
}
