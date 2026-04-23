using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class ManageTeamViewModel
{
    public int SelectedLinkshellId { get; set; }

    public List<Linkshell> Linkshells { get; set; } = new();

    public List<AppUserLinkshell> Members { get; set; } = new();

    public bool CanManage { get; set; }

    public string? SearchTerm { get; set; }

    public List<AppUser> Players { get; set; } = new();

    public List<Invite> PendingInvites { get; set; } = new();

    public List<Invite> SentInvites { get; set; } = new();

    public static readonly IReadOnlyList<string> RankOptions = new[]
    {
        "Leader",
        "Officer",
        "Member"
    };

    public static readonly IReadOnlyList<string> StatusOptions = new[]
    {
        "Active",
        "Pending",
        "Inactive"
    };
}

public class SendInviteInput
{
    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    public int LinkshellId { get; set; }
}

public class ModifyRankStatusInput
{
    [Required]
    public int Id { get; set; }

    [Required]
    [StringLength(32)]
    public string Rank { get; set; } = string.Empty;

    [Required]
    [StringLength(32)]
    public string Status { get; set; } = string.Empty;
}
