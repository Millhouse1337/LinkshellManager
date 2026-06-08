using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class LinkshellRole
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    public bool IsSystem { get; set; }

    public int SortOrder { get; set; }

    public bool CanManageRoles { get; set; }

    public bool CanManageMembers { get; set; }

    public bool CanManageEvents { get; set; }

    public bool CanModerateLiveEvent { get; set; }

    public bool CanAddLoot { get; set; }

    public bool CanManageInventory { get; set; }

    public bool CanManageTreasury { get; set; }

    public bool CanManageRules { get; set; }

    public bool CanManageAnnouncements { get; set; }

    public bool CanManageTods { get; set; }

    public bool CanAuditDkp { get; set; }

    public bool CanManageAuctions { get; set; }

    // Lock/unlock bidding across the linkshell's auctions (Linkshell.AuctionsLocked)
    // to prevent collusive overbidding from freeing a winner's committed DKP.
    public bool CanLockAuctions { get; set; }

    public bool CanCustomizeLinkshell { get; set; }

    public bool CanSubmitTodForApproval { get; set; }

    public bool CanSubmitAttendanceForApproval { get; set; }

    public bool CanManageParties { get; set; }

    public bool CanManageInvites { get; set; }

    // Permission to place bids on auctions. On for everyone by default; the built-in
    // "Trial" role has it off so trial members can attend/earn but not bid yet.
    public bool CanBid { get; set; }
}
