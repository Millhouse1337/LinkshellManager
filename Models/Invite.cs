using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Models;

// Indexed on DiscordUserId so the per-request auto-join lookup (match a freshly
// signed-in Discord user against any pending Discord-keyed invites) stays cheap.
[Index(nameof(DiscordUserId))]
public class Invite
{
    [Key]
    public int Id { get; set; }

    // Nullable now: a Discord-roster invite targets someone who has no LSM
    // account yet, so there's no AppUser to point at until they first sign in.
    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    public string Status { get; set; } = "Pending";

    // Set when the invite was sent to a Discord server member who isn't (yet) an
    // LSM user. The first time that Discord account signs into LSM, it's matched
    // here by ID and the person auto-joins the linkshell. Null for normal
    // account-targeted invites and join requests.
    [MaxLength(32)]
    public string? DiscordUserId { get; set; }

    // Snapshot of the Discord display name at invite time, so the sender's
    // pending-invite list can show who was invited before they have an account.
    [MaxLength(64)]
    public string? DiscordDisplayName { get; set; }
}
