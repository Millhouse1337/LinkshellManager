using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Turns one list-page request into the view model: runs the page query through the table's typed
// model, formats every cell, and resolves foreign keys to the principal row's label in one batch
// per principal table (never one query per row). Links only point at tables that are currently
// shown, so a hidden or unselected principal is still named but not reachable.
public sealed class DataAdminQueryService
{
    private readonly ApplicationDbContext _db;

    public DataAdminQueryService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<DataAdminListViewModel> ListAsync(DataAdminModel table, DataAdminListQuery query, IReadOnlySet<string> shownTables, CancellationToken ct)
    {
        // ILike is Postgres-only; the InMemory provider used by the tests would throw on it.
        var page = await table.QueryPageAsync(_db, query, _db.Database.IsNpgsql(), DataAdminDefaults.PageSize, ct);

        // Links re-emit only the filters this table can apply, so a stale ?f.X from a bookmark
        // does not follow every pager and sort link around invisibly.
        var applicable = query.Filters
            .Where(pair => table.FilterColumns.Any(column => column.Name == pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var model = new DataAdminListViewModel
        {
            Slug = table.Slug,
            DisplayName = table.DisplayName,
            TableName = table.TableName,
            Query = query with { Page = page.Page, Filters = applicable },
            Total = page.Total,
            Page = page.Page,
            TotalPages = page.TotalPages,
            PageSize = DataAdminDefaults.PageSize,
            SortColumn = page.SortColumn.Name,
            Descending = page.Descending,
            SearchHint = string.Join(", ", table.SearchColumns.Select(c => c.DisplayName)),
            CanCreate = table.CanCreate,
            CanEdit = table.CanEdit,
            CanDelete = table.CanDelete,
        };
        foreach (var column in table.ListColumns)
        {
            model.Columns.Add(new DataAdminListColumn
            {
                Name = column.Name,
                DisplayName = column.DisplayName,
                IsKey = column.IsKey,
                IsNumeric = IsNumeric(column),
            });
        }
        foreach (var entity in page.Rows)
        {
            var row = new DataAdminListRow
            {
                Key = table.KeyToString(table.KeyOf(entity)),
                Label = table.LabelOf(entity),
            };
            foreach (var column in table.ListColumns)
            {
                var raw = column.Property.GetValue(entity);
                row.Cells.Add(new DataAdminListCell
                {
                    Raw = raw,
                    Text = DataAdminFormat.Display(raw, truncate: true),
                    IsEmpty = raw is null,
                    IsNumeric = IsNumeric(column),
                });
            }
            model.Rows.Add(row);
        }

        await FillForeignKeyLabelsAsync(table.ListColumns, model.Rows, shownTables, ct);
        await AddActiveFiltersAsync(table, query, model, ct);
        return model;
    }

    // For each foreign-key column: one label query per principal table for the whole page, then
    // every cell shows the principal's label (or the raw key when the row is gone) and links to
    // the principal's own list, narrowed to that key, when the principal table is shown.
    private async Task FillForeignKeyLabelsAsync(IReadOnlyList<DataAdminColumn> columns, List<DataAdminListRow> rows, IReadOnlySet<string> shownTables, CancellationToken ct)
    {
        for (var index = 0; index < columns.Count; index++)
        {
            var principal = columns[index].ForeignKeyTo;
            if (principal is null)
            {
                continue;
            }
            var keys = rows.Select(row => row.Cells[index].Raw).OfType<object>().Distinct().ToList();
            var labels = await principal.LoadLabelsAsync(_db, keys, ct);
            var linkable = shownTables.Contains(principal.ClrName);
            foreach (var row in rows)
            {
                var cell = row.Cells[index];
                if (cell.Raw is null)
                {
                    continue;
                }
                var keyText = principal.KeyToString(cell.Raw);
                cell.Text = labels.TryGetValue(keyText, out var label) ? label : keyText;
                if (linkable)
                {
                    cell.LinkSlug = principal.Slug;
                    cell.LinkKey = keyText;
                }
            }
        }
    }

    // One row: every visible column (foreign keys resolved to the principal's label and linked to
    // its details when that table is shown), then a count per table whose rows point at this row.
    // Null when the key does not exist.
    public async Task<DataAdminDetailsViewModel?> DetailsAsync(DataAdminModel table, object key, IReadOnlySet<string> shownTables, CancellationToken ct)
    {
        var entity = await table.FindAsync(_db, key, track: false, ct);
        if (entity is null)
        {
            return null;
        }

        var keyText = table.KeyToString(key);
        var model = new DataAdminDetailsViewModel
        {
            Slug = table.Slug,
            DisplayName = table.DisplayName,
            TableName = table.TableName,
            Key = keyText,
            Label = table.LabelOf(entity),
            CanEdit = table.CanEdit,
            CanDelete = table.CanDelete,
            DiscordSideEffect = table.Policy.DiscordSideEffect,
        };

        foreach (var column in table.DetailColumns)
        {
            var raw = column.Property.GetValue(entity);
            var field = new DataAdminDetailField
            {
                Name = column.Name,
                DisplayName = column.DisplayName,
                Text = DataAdminFormat.Display(raw),
                IsEmpty = raw is null,
                IsLongText = raw is string text && (text.Length > DataAdminDefaults.ListCellMaxLength || text.Contains('\n')),
            };
            if (raw is not null && column.ForeignKeyTo is { } principal)
            {
                var principalKey = principal.KeyToString(raw);
                var labels = await principal.LoadLabelsAsync(_db, new[] { raw }, ct);
                field.Text = labels.TryGetValue(principalKey, out var label) ? label : principalKey;
                field.Note = $"{principal.DisplayName} #{principalKey}";
                if (shownTables.Contains(principal.ClrName))
                {
                    field.LinkSlug = principal.Slug;
                    field.LinkKey = principalKey;
                }
            }
            model.Fields.Add(field);
        }

        foreach (var relation in table.ReverseRelations)
        {
            var count = await relation.Dependent.CountWhereAsync(_db, relation.ForeignKeyColumn, key, ct);
            model.Related.Add(new DataAdminRelatedTable
            {
                Slug = relation.Dependent.Slug,
                DisplayName = relation.Dependent.DisplayName,
                ForeignKeyColumn = relation.ForeignKeyColumn.Name,
                ForeignKeyDisplayName = relation.ForeignKeyColumn.DisplayName,
                Count = count,
                OnDelete = DescribeDeleteBehavior(relation.DeleteBehavior),
                IsShown = shownTables.Contains(relation.Dependent.ClrName),
                FilterRoute = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [DataAdminListQuery.FilterPrefix + relation.ForeignKeyColumn.Name] = keyText,
                },
            });
        }

        return model;
    }

    // Postgres semantics for a delete that tracks only the one row: Cascade and SetNull are DB
    // actions; everything else (Restrict, NoAction, EF's client-side behaviours) is a constraint
    // the database will refuse to break.
    public static string DescribeDeleteBehavior(DeleteBehavior behavior) => behavior switch
    {
        DeleteBehavior.Cascade => "Deleted together with this row",
        DeleteBehavior.SetNull => "Kept, but unlinked, when this row is deleted",
        _ => "Blocks deleting this row while any exist",
    };

    // The filter chips: "Linkshell: Kraken LS" rather than "LinkshellId: 5" when the principal is known.
    private async Task AddActiveFiltersAsync(DataAdminModel table, DataAdminListQuery query, DataAdminListViewModel model, CancellationToken ct)
    {
        foreach (var (name, raw) in query.Filters)
        {
            var column = table.FilterColumns.FirstOrDefault(c => c.Name == name);
            if (column is null)
            {
                continue;
            }
            var label = raw;
            var displayName = column.DisplayName;
            if (column.ForeignKeyTo is { } principal)
            {
                displayName = principal.DisplayName;
                if (principal.TryParseKey(raw, out var key))
                {
                    var labels = await principal.LoadLabelsAsync(_db, new[] { key }, ct);
                    label = labels.TryGetValue(principal.KeyToString(key), out var found) ? found : raw;
                }
            }
            model.ActiveFilters.Add(new DataAdminActiveFilter { Column = name, DisplayName = displayName, Value = raw, Label = label });
        }
    }

    private static bool IsNumeric(DataAdminColumn column)
    {
        var type = column.UnderlyingType;
        return type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(double)
            || type == typeof(float) || type == typeof(decimal);
    }
}
