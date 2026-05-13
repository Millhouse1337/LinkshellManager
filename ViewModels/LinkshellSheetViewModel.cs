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
}
