namespace LinkshellManagerDiscordApp.ViewModels;

public class DkpHistoryViewModel
{
    public int? SelectedLinkshellId { get; set; }
    public string? SelectedLinkshellName { get; set; }
    public string? SelectedAppUserId { get; set; }
    public string? SelectedMemberName { get; set; }
    public double CurrentBalance { get; set; }
    public List<DkpHistoryLinkshellOptionViewModel> Linkshells { get; set; } = new();
    public List<DkpHistoryMemberOptionViewModel> Members { get; set; } = new();
    public List<DkpHistoryEntryViewModel> Entries { get; set; } = new();
}

public class DkpHistoryLinkshellOptionViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class DkpHistoryMemberOptionViewModel
{
    public string AppUserId { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public double CurrentBalance { get; set; }
}

public class DkpHistoryEntryViewModel
{
    public int Id { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public double Amount { get; set; }
    public double RunningBalance { get; set; }
    public DateTime? OccurredAt { get; set; }
    public string? EventName { get; set; }
    public string? EventType { get; set; }
    public string? EventLocation { get; set; }
    public DateTime? EventStartTime { get; set; }
    public DateTime? EventEndTime { get; set; }
    public string? ItemName { get; set; }
    public string? Details { get; set; }
}
