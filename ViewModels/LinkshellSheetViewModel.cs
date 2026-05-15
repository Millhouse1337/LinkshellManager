using LinkshellManagerDiscordApp.Services;

namespace LinkshellManagerDiscordApp.ViewModels;

public sealed class LinkshellSheetViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public string? SpreadsheetId { get; set; }
    public string? TabName { get; set; }
    public bool IsOAuthConfigured { get; set; }
    public bool IsOAuthConnected { get; set; }
    public string? ConnectedGoogleEmail { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public bool SheetSyncEnabled { get; set; }

    public string? AttInputTabName { get; set; }
    public string? AttInputDefaultEntryType { get; set; }
    public string? ManualPointsTabName { get; set; }

    public string? PreviewRange { get; set; }
    public ImportPreview? Preview { get; set; }
}
