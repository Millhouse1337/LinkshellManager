using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LinkshellManagerDiscordApp.Services;

namespace LinkshellManagerDiscordApp.Models;

/// <summary>
/// One member saying they want something off a Charts board.
///
/// A different feature from ChartPopItem, not a status on it. A pop item is a fact about the
/// linkshell's stock; a request is a fact about a PERSON, it is written by that person rather than
/// by an officer, and it has no holder, no quantity held and no farming credit. Sharing a table
/// would have meant a nullable half of every column and a Kind that changes what the other half
/// means.
///
/// This is also the first Charts write a plain member may make. Everything else on these boards
/// needs CanManageCharts; a request needs only membership, and the ownership rule that follows from
/// that lives in ChartWishlistService.CanEditRequest so both surfaces enforce one copy of it.
/// </summary>
public class ChartWishlistRequest
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    /// <summary>Board key from <see cref="ChartBoardCatalog"/> - "Dynamis", "Limbus", "HENM".</summary>
    [MaxLength(16)]
    public string Board { get; set; } = string.Empty;

    /// <summary>
    /// The card this request is tied to, or NULL for "anywhere on this board".
    ///
    /// Optional because both readings are real: somebody wants a specific drop out of Xarcabard, and
    /// somebody else just wants a Ridill from wherever it turns up. A null Boss contributes to NO
    /// card badge and appears only in the board's request list, which is the honest rendering of
    /// "I did not say where".
    /// </summary>
    [MaxLength(64)]
    public string? Boss { get; set; }

    [MaxLength(128)]
    public string ItemName { get; set; } = string.Empty;

    public int Quantity { get; set; } = 1;

    [MaxLength(512)]
    public string? Notes { get; set; }

    /// <summary>ChartWishlistStatuses.Pending or Fulfilled.</summary>
    [MaxLength(16)]
    public string Status { get; set; } = ChartWishlistStatuses.Pending;

    /// <summary>Officer-set order within the board. Lower sorts first.</summary>
    public int Priority { get; set; }

    // WHO IT IS FOR. The AppUserId is the OWNERSHIP KEY, not the membership id: a membership row is
    // re-created on unsync and resync, so an id that identified somebody last month may identify
    // nobody today, and "may I withdraw this" would silently start answering no. The AppUserId is
    // also what both write paths already hold. Plain string with no FK, exactly like
    // ChartPopItem.CreatedByAppUserId.
    [MaxLength(450)]
    public string? RequestedByAppUserId { get; set; }

    // Convenience for the roster join only, and deliberately a plain nullable int with no
    // relationship: Linkshells already cascades into both AppUserLinkshells and this table, so an FK
    // here would be a second cascade path into it. Same call, and the same reason, as
    // ChartPopItem.HeldByMembershipId.
    public int? RequestedByMembershipId { get; set; }

    /// <summary>The name the list shows. The NAME is the record, the ids are conveniences.</summary>
    [MaxLength(256)]
    public string RequestedByCharacterName { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Set when an officer ticks it off. There is no third "Withdrawn" status: withdrawing DELETES
    // the row, because a withdrawn request is a thing nobody ever reads again and keeping one would
    // mean every list query filters it out forever. An officer removing somebody else's request is
    // the same operation, so one endpoint covers both.
    public DateTime? FulfilledAt { get; set; }

    [MaxLength(450)]
    public string? FulfilledByAppUserId { get; set; }

    [MaxLength(256)]
    public string? FulfilledByCharacterName { get; set; }
}
