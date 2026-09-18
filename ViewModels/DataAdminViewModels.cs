namespace LinkshellManagerDiscordApp.ViewModels;

// View models for the Data Admin pages. Views bind to these, never to the engine's own types, so
// a Razor file cannot reach a DbContext or an expression builder by accident.

public sealed class DataAdminIndexViewModel
{
    public List<DataAdminIndexGroup> Groups { get; } = new();
    public int ShownCount { get; set; }
    public int CatalogCount { get; set; }
}

// Shown tables grouped under the root of their cascade-delete tree (Linkshell, App User, ...).
public sealed class DataAdminIndexGroup
{
    public required string RootName { get; init; }
    public List<DataAdminIndexCard> Cards { get; } = new();
}

public sealed class DataAdminIndexCard
{
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public required string TableName { get; init; }
    public long RowCount { get; init; }
    public bool CanCreate { get; init; }
    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }
    public string? DiscordSideEffect { get; init; }
}

public sealed class DataAdminTablesViewModel
{
    public List<DataAdminTableNode> Roots { get; } = new();
    public bool ShowCounts { get; set; }
    public int SelectedCount { get; set; }
    public int TotalCount { get; set; }
}

// One row of the chooser tree. Rendered by the recursive ~/Views/DataAdmin/_TableNode.cshtml.
public sealed class DataAdminTableNode
{
    public required string ClrName { get; init; }
    public required string DisplayName { get; init; }
    public required string TableName { get; init; }
    public required string Slug { get; init; }
    // Slash-joined CLR names from the root down, so the "tick group" helper can find descendants.
    public required string Path { get; init; }
    public required int Depth { get; init; }
    public bool Selected { get; init; }
    public bool ReadOnly { get; init; }
    public bool NoCreate { get; init; }
    public bool NoDelete { get; init; }
    public string? DiscordSideEffect { get; init; }
    public long? RowCount { get; init; }
    public IReadOnlyList<string> AlsoDeletedWith { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CascadeDescendants { get; init; } = Array.Empty<string>();
    public List<DataAdminTableNode> Children { get; } = new();
}
