using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// Someone asked to take over an imported (placeholder) member by NAME. Character names are public
// (boards, the posted DKP sheet), so a name match alone used to hand over that member's DKP and
// roster spot to whoever typed it first. Now it waits here until an officer who can manage members
// approves or denies it (PlaceholderClaimRequestService).
//
// Two kinds of requester:
//   * RequesterAppUserId: a signed-in account pressed "Claim" on the "Claim your DKP" prompt.
//     Approving runs the claim (PlaceholderClaimService) onto that account.
//   * RequesterDiscordUserId: a player with no account typed that name in the Discord sign-up form.
//     Approving links the placeholder to their Discord id, so their next Sign Up uses it and their
//     first LSM launch takes it over (DiscordIdentityService).
public class PlaceholderClaimRequest
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // No foreign key on purpose: the placeholder account is deleted when a claim succeeds, and this
    // row is kept as the record of who asked for it.
    [MaxLength(450)]
    public string PlaceholderAppUserId { get; set; } = string.Empty;

    [MaxLength(450)]
    public string? RequesterAppUserId { get; set; }

    [MaxLength(32)]
    public string? RequesterDiscordUserId { get; set; }

    // What officers see: the account's character name, or the Discord display name.
    [MaxLength(256)]
    public string? RequesterName { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    // Null while pending.
    public DateTime? ResolvedAtUtc { get; set; }

    // Null while pending; true = approved, false = denied (or closed because someone else claimed it).
    public bool? Approved { get; set; }

    [MaxLength(450)]
    public string? ResolvedByAppUserId { get; set; }
}
