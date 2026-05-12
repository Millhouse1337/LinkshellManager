using System.ComponentModel.DataAnnotations;

namespace LinkshellManagerDiscordApp.ViewModels;

public class LootHistoryIndexViewModel
{
    public const int DefaultPageSize = 25;

    public int? SelectedLinkshellId { get; set; }
    public string? SelectedLinkshellName { get; set; }
    public string SourceFilter { get; set; } = "all";
    public string? WinnerFilter { get; set; }
    public string? ItemFilter { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = DefaultPageSize;
    public int TotalCount { get; set; }
    public int TotalPages => TotalCount == 0
        ? 1
        : (int)Math.Ceiling(TotalCount / (double)Math.Max(1, PageSize));
    public bool CanEdit { get; set; }
    public List<LootHistoryEntryViewModel> Entries { get; set; } = new();
}

public class LootHistoryEntryViewModel
{
    public int LootDetailId { get; set; }
    // "Tod" or "Event" — drives source-specific routing on the Edit button.
    public string Source { get; set; } = "Tod";
    public int ParentId { get; set; }
    public string? Context { get; set; }
    public DateTime? OccurredAt { get; set; }
    public string? ItemName { get; set; }
    public string? ItemWinner { get; set; }
    public int? WinningDkpSpent { get; set; }
    public double? ActualDeductedDkp { get; set; }
    public bool IsEdited => EditedAt.HasValue;
    public string? LastEditReason { get; set; }
    public DateTime? EditedAt { get; set; }
    public string? EditedByCharacterName { get; set; }
}

public class LootHistoryEditViewModel
{
    public int LootDetailId { get; set; }
    public string Source { get; set; } = "Tod";
    public string? Context { get; set; }
    public string? CurrentItemName { get; set; }
    public string? CurrentItemWinner { get; set; }
    public int? CurrentWinningDkpSpent { get; set; }
    public string? LinkshellLootStructure { get; set; }

    [Required(ErrorMessage = "Item name is required.")]
    [MaxLength(256)]
    public string? ItemName { get; set; }

    [Required(ErrorMessage = "Winner is required.")]
    [MaxLength(256)]
    public string? ItemWinner { get; set; }

    [Required(ErrorMessage = "DKP amount is required.")]
    [Range(0, int.MaxValue, ErrorMessage = "DKP amount must be 0 or greater.")]
    public int? WinningDkpSpent { get; set; }

    [Required(ErrorMessage = "An edit reason is required.")]
    [MaxLength(512)]
    public string? Reason { get; set; }

    public List<string> RosterCharacterNames { get; set; } = new();
}
