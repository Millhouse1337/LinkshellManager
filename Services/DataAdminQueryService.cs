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

        var model = new DataAdminListViewModel
        {
            Slug = table.Slug,
            DisplayName = table.DisplayName,
            TableName = table.TableName,
            Query = query with { Page = page.Page },
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
                    cell.LinkRoute = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [DataAdminListQuery.FilterPrefix + principal.Key.Name] = keyText,
                    };
                }
            }
        }
    }

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
