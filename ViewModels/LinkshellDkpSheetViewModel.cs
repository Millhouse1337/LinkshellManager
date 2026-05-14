namespace LinkshellManagerDiscordApp.ViewModels;

public sealed class LinkshellDkpSheetViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public string? SpreadsheetId { get; set; }
    public string? EmbedUrl { get; set; }
    public string? OpenInSheetsUrl { get; set; }
    public bool IsConfigured { get; set; }
}
