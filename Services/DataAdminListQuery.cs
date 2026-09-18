using Microsoft.AspNetCore.Http;

namespace LinkshellManagerDiscordApp.Services;

// The list page's query string, parsed once: ?q=<search>&sort=<Column>&desc=true&page=2&f.<Column>=<value>.
// Filters use a dotted prefix, which MVC's dictionary binder does not understand, so this is
// parsed by hand from the raw query and re-emitted by ToRouteValues() for every pager/sort link
// (the house rule from Views/ManageTeam/Index.cshtml: every link re-emits every filter).
public sealed record DataAdminListQuery
{
    public const string SearchKey = "q";
    public const string SortKey = "sort";
    public const string DescendingKey = "desc";
    public const string PageKey = "page";
    public const string FilterPrefix = "f.";

    public string? Search { get; init; }
    public string? Sort { get; init; }
    public bool Descending { get; init; }
    public int Page { get; init; } = 1;
    public IReadOnlyDictionary<string, string> Filters { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public static DataAdminListQuery Parse(IQueryCollection query)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, values) in query)
        {
            if (!key.StartsWith(FilterPrefix, StringComparison.Ordinal) || key.Length <= FilterPrefix.Length)
            {
                continue;
            }
            var value = values.ToString().Trim();
            if (value.Length > 0)
            {
                filters[key[FilterPrefix.Length..]] = value;
            }
        }

        var page = int.TryParse(query[PageKey], out var parsed) && parsed > 0 ? parsed : 1;
        var descending = string.Equals(query[DescendingKey], "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(query[DescendingKey], "1", StringComparison.Ordinal);
        return new DataAdminListQuery
        {
            Search = Clean(query[SearchKey]),
            Sort = Clean(query[SortKey]),
            Descending = descending,
            Page = page,
            Filters = filters,
        };
    }

    // The same state as route values (page 1 and defaults are omitted so URLs stay short).
    public Dictionary<string, string> ToRouteValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            values[SearchKey] = Search;
        }
        if (!string.IsNullOrWhiteSpace(Sort))
        {
            values[SortKey] = Sort;
            if (Descending)
            {
                values[DescendingKey] = "true";
            }
        }
        if (Page > 1)
        {
            values[PageKey] = Page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (var (column, value) in Filters)
        {
            values[FilterPrefix + column] = value;
        }
        return values;
    }

    public DataAdminListQuery WithPage(int page) => this with { Page = page };

    // Clicking a column header sorts by it ascending, again to flip, and always returns to page 1.
    public DataAdminListQuery WithSort(string column) =>
        this with { Sort = column, Descending = string.Equals(Sort, column, StringComparison.Ordinal) && !Descending, Page = 1 };

    public DataAdminListQuery WithoutFilter(string column)
    {
        var filters = new Dictionary<string, string>(Filters, StringComparer.Ordinal);
        filters.Remove(column);
        return this with { Filters = filters, Page = 1 };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

// One page of entities from DataAdminModel.QueryPageAsync, before the query service turns them
// into cells. Page is the clamped page actually returned.
public sealed record DataAdminPageResult(
    IReadOnlyList<object> Rows,
    long Total,
    int Page,
    int TotalPages,
    DataAdminColumn SortColumn,
    bool Descending);
