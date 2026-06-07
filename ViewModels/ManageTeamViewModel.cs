using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;

namespace LinkshellManagerDiscordApp.ViewModels;

public class ManageTeamViewModel
{
    public int SelectedLinkshellId { get; set; }

    public List<Linkshell> Linkshells { get; set; } = new();

    public List<AppUserLinkshell> Members { get; set; } = new();

    public bool CanManage { get; set; }

    public string? SearchTerm { get; set; }

    // App Sync filter: when true (the default), the Members list is limited to
    // rows that have an AppUserId AND a non-Pending/Unclaimed Status -- i.e.,
    // members the linkshell actually has app + DKP tracking on. Flipping it
    // off shows every AppUserLinkshell row, including sheet-only placeholders.
    public bool AppSyncOnly { get; set; } = true;

    // Members list pagination (View Team). SearchTerm above doubles as the
    // member-name filter on the Index page.
    public const int MembersPageSize = 10;

    public int PageNumber { get; set; } = 1;

    public int PageSize { get; set; } = MembersPageSize;

    // Count after the search filter is applied — drives pagination.
    public int TotalCount { get; set; }

    // Unfiltered linkshell member count — shown in the page header.
    public int TotalMembers { get; set; }

    public int TotalPages => TotalCount == 0
        ? 1
        : (int)Math.Ceiling(TotalCount / (double)Math.Max(1, PageSize));

    public List<AppUser> Players { get; set; } = new();

    // Add-members browse: paginated invite candidates (shared with the Discord
    // Activity via InviteCandidateService) and the affiliation filter applied.
    public List<InviteCandidate> Candidates { get; set; } = new();

    // "all" (default), "unaffiliated" (no linkshell), or "affiliated".
    public string? Filter { get; set; }

    public List<Invite> PendingInvites { get; set; } = new();

    public List<Invite> SentInvites { get; set; } = new();

    // User-initiated "request to join" submissions for linkshells the caller
    // manages (created via the Discord Activity); approved/declined on the web.
    public List<Invite> JoinRequests { get; set; } = new();

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
