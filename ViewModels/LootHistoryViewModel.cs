using System.ComponentModel.DataAnnotations;

namespace LinkshellManagerDiscordApp.ViewModels;

public class LootHistoryIndexViewModel
{
    public const int DefaultPageSize = 25;

    public int? SelectedLinkshellId { get; set; }
    public string? SelectedLinkshellName { get; set; }

    // Single free-text filter matched (case-insensitive, substring) against
    // BOTH the winner character name and the item name. Replaces the former
    // separate WinnerFilter / ItemFilter pair.
    public string? QueryFilter { get; set; }
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

// Backs the standalone "Add Loot" form.
//
// A submission now writes an EventLootDetail filed against a LIVE event, a PAST event, or nothing
// at all. It used to mint a throwaway ToD per submission and hang a TodLootDetail off it, which is
// why every hand-entered drop showed up in history as source "ToD" and why the ToD Tracker had to
// be defended against those rows hijacking a monster card.
public class LootAddViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }

    // Which KIND of event this loot came from: "none", "live" or "past". A live event and a past
    // one are different tables (Event vs EventHistory), so the kind picks which id below is read.
    public string SourceKind { get; set; } = "none";

    public int? EventId { get; set; }
    public int? EventHistoryId { get; set; }

    // Options for the two pickers. Past events are the recent ones plus whatever a search turns
    // up — a linkshell accumulates hundreds, so the full list is never rendered.
    public List<LootEventOption> LiveEvents { get; set; } = new();
    public List<LootEventOption> PastEvents { get; set; } = new();

    // What the officer typed into the past-event search. Echoed back so the box keeps its text
    // across the round trip that widens the list.
    [MaxLength(128)]
    public string? EventQuery { get; set; }

    [Required(ErrorMessage = "Item name is required.")]
    [MaxLength(256)]
    public string? ItemName { get; set; }

    [Required(ErrorMessage = "Winner is required.")]
    [MaxLength(256)]
    public string? ItemWinner { get; set; }

    [Required(ErrorMessage = "DKP amount is required.")]
    [Range(0, int.MaxValue, ErrorMessage = "DKP amount must be 0 or greater.")]
    public int? WinningDkpSpent { get; set; }

    public string? LinkshellLootStructure { get; set; }
    public List<string> RosterCharacterNames { get; set; } = new();

    // Which DKP pool this loot is paid from. Manual/ToD loot has a monster, not an event type, so
    // the pool can't be derived — it defaults to whichever pool "HNM" maps to and the officer can
    // override it. Null (and the picker hidden) on a linkshell that hasn't split its DKP.
    public int? DkpPoolId { get; set; }
    public List<LootDkpPoolOption> DkpPools { get; set; } = new();
    public bool HasMultiplePools => DkpPools.Count > 1;
}

// One selectable event on the Add loot form.
public class LootEventOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    // "Sky - Aug 25" style suffix, so two runs of the same event are tellable apart.
    public string? Detail { get; set; }
}

public class LootDkpPoolOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
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
