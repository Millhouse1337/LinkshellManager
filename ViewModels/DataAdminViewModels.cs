using LinkshellManagerDiscordApp.Services;

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

// One list page. The route helpers re-emit the whole query (search, sort, filters) so no link
// ever drops a filter -- the house rule from Views/ManageTeam/Index.cshtml, generalised.
public sealed class DataAdminListViewModel
{
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public required string TableName { get; init; }
    public required DataAdminListQuery Query { get; init; }
    public List<DataAdminListColumn> Columns { get; } = new();
    public List<DataAdminListRow> Rows { get; } = new();
    public List<DataAdminActiveFilter> ActiveFilters { get; } = new();
    public long Total { get; init; }
    public int Page { get; init; }
    public int TotalPages { get; init; }
    public int PageSize { get; init; }
    public required string SortColumn { get; init; }
    public bool Descending { get; init; }
    // "RuleTitle, RuleDetails, Category" for the search box placeholder; empty when nothing is searchable.
    public string SearchHint { get; init; } = string.Empty;
    public bool CanCreate { get; init; }
    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }

    public long FirstRowNumber => Total == 0 ? 0 : (long)(Page - 1) * PageSize + 1;
    public long LastRowNumber => Math.Min(Total, (long)Page * PageSize);

    public Dictionary<string, string> PageRoute(int page) => Query.WithPage(page).ToRouteValues();
    public Dictionary<string, string> SortRoute(string column) => Query.WithSort(column).ToRouteValues();
    public Dictionary<string, string> RemoveFilterRoute(string column) => Query.WithoutFilter(column).ToRouteValues();
}

public sealed class DataAdminListColumn
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public bool IsKey { get; init; }
    public bool IsNumeric { get; init; }
}

public sealed class DataAdminListRow
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public List<DataAdminListCell> Cells { get; } = new();
}

public sealed class DataAdminListCell
{
    public object? Raw { get; init; }
    public string Text { get; set; } = string.Empty;
    public bool IsEmpty { get; init; }
    public bool IsNumeric { get; init; }
    // Set when the cell is a foreign key to a table that is currently shown: the principal row's details page.
    public string? LinkSlug { get; set; }
    public string? LinkKey { get; set; }
}

// One row's details page: every visible column, then the tables whose rows point at this row.
public sealed class DataAdminDetailsViewModel
{
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public required string TableName { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public List<DataAdminDetailField> Fields { get; } = new();
    public List<DataAdminRelatedTable> Related { get; } = new();
    public bool CanEdit { get; init; }
    public bool CanDelete { get; init; }
    public string? DiscordSideEffect { get; init; }
}

public sealed class DataAdminDetailField
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string Text { get; set; } = string.Empty;
    public bool IsEmpty { get; init; }
    public bool IsLongText { get; init; }
    // "Linkshell #5" beside a foreign key's label, so the raw key stays visible.
    public string? Note { get; set; }
    public string? LinkSlug { get; set; }
    public string? LinkKey { get; set; }
}

// The create/edit form: one field per editable column, posted back as values[<Name>].
public sealed class DataAdminEditViewModel
{
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public bool IsCreate { get; init; }
    public string? Key { get; init; }
    public string? Label { get; init; }
    public List<DataAdminFieldViewModel> Fields { get; } = new();
    // Errors not tied to one field (a database refusal).
    public List<string> FormErrors { get; } = new();
    public string? DiscordSideEffect { get; init; }
}

public sealed class DataAdminFieldViewModel
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string Value { get; init; } = string.Empty;
    // text | textarea | number | checkbox | datetime-local | select
    public string InputType { get; set; } = "text";
    public bool IsRequired { get; init; }
    public int? MaxLength { get; init; }
    public bool IsKey { get; init; }
    public string? Hint { get; set; }
    public string? Error { get; init; }
    public IReadOnlyList<DataAdminSelectOption>? Options { get; set; }
}

public sealed record DataAdminSelectOption(string Value, string Text);

// The delete confirmation: what else goes, what is unlinked, and what blocks the delete.
public sealed class DataAdminDeleteViewModel
{
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public bool IsBlocked { get; init; }
    public bool IsTruncated { get; init; }
    public long DeletedRows { get; init; }
    public string? DiscordSideEffect { get; init; }
    public List<DataAdminImpactNode> Children { get; } = new();
    public List<DataAdminImpactTotal> Totals { get; } = new();
}

public sealed class DataAdminImpactNode
{
    public required string DisplayName { get; init; }
    public required string Slug { get; init; }
    public required string ForeignKeyDisplayName { get; init; }
    // deleted | unlinked | blocked
    public required string Effect { get; init; }
    public long Count { get; init; }
    public long AlreadyCounted { get; init; }
    public bool Overflow { get; init; }
    public bool DepthCapped { get; init; }
    public List<DataAdminImpactNode> Children { get; } = new();
}

public sealed record DataAdminImpactTotal(string DisplayName, long Rows);

// A table with a foreign key into the row being shown, with how many of its rows point here.
public sealed class DataAdminRelatedTable
{
    public required string Slug { get; init; }
    public required string DisplayName { get; init; }
    public required string ForeignKeyColumn { get; init; }
    public required string ForeignKeyDisplayName { get; init; }
    public long Count { get; init; }
    // What happens to those rows when this row is deleted, in words.
    public required string OnDelete { get; init; }
    public bool IsShown { get; init; }
    // ?f.<ForeignKeyColumn>=<key> for the dependent's list page.
    public required Dictionary<string, string> FilterRoute { get; init; }
}

// A chip on the list page: "Linkshell: Kraken LS" for ?f.LinkshellId=5.
public sealed class DataAdminActiveFilter
{
    public required string Column { get; init; }
    public required string DisplayName { get; init; }
    public required string Value { get; init; }
    public required string Label { get; init; }
}
