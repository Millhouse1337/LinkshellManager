using System.ComponentModel.DataAnnotations;

namespace LinkshellManagerDiscordApp.ViewModels;

public class DkpAdjustmentViewModel
{
    public int SelectedLinkshellId { get; set; }
    public string? SelectedLinkshellName { get; set; }
    // SkySeaDynamis | HnmOnly | Both. Drives whether the snapshot-credit ("Add to
    // a previous entry") audit option is offered — it's HNM-only.
    public string? SelectedLinkshellType { get; set; }
    public bool CanManage { get; set; }
    public List<DkpAdjustmentLinkshellOption> Linkshells { get; set; } = new();
    public List<DkpAdjustmentMemberRow> Members { get; set; } = new();
}

public class DkpAdjustmentLinkshellOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class DkpAdjustmentMemberRow
{
    public int MembershipId { get; set; }
    public string AppUserId { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public string? Rank { get; set; }
    public double CurrentBalance { get; set; }
}

public class DkpAdjustmentInput
{
    [Required]
    public int MembershipId { get; set; }

    [Required]
    public int LinkshellId { get; set; }

    [Required]
    [Range(-100000, 100000)]
    public double Amount { get; set; }

    [Required]
    [StringLength(1024, MinimumLength = 1)]
    public string Reason { get; set; } = string.Empty;
}
