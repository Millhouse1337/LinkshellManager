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
/// surfaces could word differently. Same call ChartBossProgress makes: no row at all is the normal
/// state, not a missing one.
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

    // Plain int with no relationship: Linkshells already cascades into AppUserLinkshells, so an FK
    // from here would be a second cascade path into this table. Same reason as
    // ChartPopItem.HeldByMembershipId and ChartPopItemCredit.LinkshellId.
    //
    // The consequence, accepted deliberately: removing somebody from the roster leaves their rows
    // behind. They never render - ChartKeyItemService.BuildGrid is roster-driven - and re-adding the
    // same person restores what they had, which is the friendlier of the two failures.
    public int MembershipId { get; set; }

    /// <summary>Denormalised so the grid and the "still needed by" list need no join back.</summary>
    [MaxLength(256)]
    public string CharacterName { get; set; } = string.Empty;

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
