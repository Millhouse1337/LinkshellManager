namespace LinkshellManagerDiscordApp.ViewModels;

public sealed class LinkshellReconciliationViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public bool IsSheetConfigured { get; set; }
    public string? ErrorMessage { get; set; }

    public List<string> SheetColumnHeaders { get; set; } = new();
    public List<ReconciliationRow> Rows { get; set; } = new();
    public List<DiscordUserOption> AssignableUsers { get; set; } = new();
    public List<DiscordUserSummary> DiscordUsersWithoutSheetRow { get; set; } = new();
}

public sealed class ReconciliationRow
{
    public string SheetCharacterName { get; set; } = string.Empty;
    public double SheetDkp { get; set; }
    public List<string> SheetCells { get; set; } = new();
    public string? SuggestedAppUserId { get; set; }
    public bool IsConfirmed { get; set; }
    public string? CurrentlyLinkedDiscordDisplay { get; set; }
    public int? PlaceholderMembershipId { get; set; }
}

public sealed class DiscordUserOption
{
    public string AppUserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? CharacterName { get; set; }
    public string? AltCharacterName1 { get; set; }
    public string? AltCharacterName2 { get; set; }
}

public sealed class DiscordUserSummary
{
    public string AppUserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? CharacterName { get; set; }
    public string? AltCharacterName1 { get; set; }
    public string? AltCharacterName2 { get; set; }
    public string? Rank { get; set; }
    public double LinkshellDkp { get; set; }
}
