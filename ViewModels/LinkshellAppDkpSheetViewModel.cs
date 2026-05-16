namespace LinkshellManagerDiscordApp.ViewModels;

public sealed class LinkshellAppDkpSheetViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }
    public bool IsConfigured { get; set; }
    public string? ErrorMessage { get; set; }
    public string? OpenInSheetsUrl { get; set; }
    public List<AppDkpSheetTabViewModel> Tabs { get; set; } = new();
}

public sealed class AppDkpSheetTabViewModel
{
    public string Name { get; set; } = string.Empty;
    public List<string> Headers { get; set; } = new();
    public List<AppDkpSheetRowViewModel> Rows { get; set; } = new();
    public int ColumnCount { get; set; }
}

public sealed class AppDkpSheetRowViewModel
{
    public int RowNumber { get; set; }
    public List<string> Cells { get; set; } = new();
}
