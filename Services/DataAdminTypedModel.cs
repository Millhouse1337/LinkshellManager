using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LinkshellManagerDiscordApp.Services;

// The one generic type in the Data Admin engine. Every typed LINQ call and every hand-built
// expression tree lives here; DataAdminModel.Create closes it over each table at startup so the
// rest of the engine never touches reflection per request.
public sealed class DataAdminModel<TEntity, TKey> : DataAdminModel
    where TEntity : class
    where TKey : notnull
{
    // Shared parameter for every expression this model builds ("e" in the generated lambdas).
    private static readonly ParameterExpression Row = Expression.Parameter(typeof(TEntity), "e");

    private readonly Expression<Func<TEntity, TKey>> _keySelector;
    private readonly Func<TEntity, TKey> _keyGetter;
    private readonly Func<TEntity, string?> _labelGetter;
    private readonly Expression<Func<TEntity, DataAdminLabelRow<TKey>>> _labelProjection;

    public DataAdminModel(IEntityType entityType, DataAdminEntityPolicy policy)
        : base(entityType, policy)
    {
        var keyBody = Expression.Property(Row, Key.Property);
        _keySelector = Expression.Lambda<Func<TEntity, TKey>>(keyBody, Row);
        _keyGetter = _keySelector.Compile();

        // Label = first non-null label column (COALESCE server-side), or null when the table has none.
        var labelBody = LabelBody();
        _labelGetter = Expression.Lambda<Func<TEntity, string?>>(labelBody, Row).Compile();
        var rowType = typeof(DataAdminLabelRow<TKey>);
        _labelProjection = Expression.Lambda<Func<TEntity, DataAdminLabelRow<TKey>>>(
            Expression.MemberInit(
                Expression.New(rowType),
                Expression.Bind(rowType.GetProperty(nameof(DataAdminLabelRow<TKey>.Key))!, keyBody),
                Expression.Bind(rowType.GetProperty(nameof(DataAdminLabelRow<TKey>.Label))!, labelBody)),
            Row);
    }

    public override Type KeyType => typeof(TKey);

    public override bool TryParseKey(string? raw, [NotNullWhen(true)] out object? key) =>
        DataAdminKeys.TryParse(typeof(TKey), raw, out key);

    public override string KeyToString(object key) =>
        Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty;

    public override object KeyOf(object entity) => _keyGetter((TEntity)entity);

    public override string LabelOf(object entity)
    {
        var label = _labelGetter((TEntity)entity);
        return string.IsNullOrWhiteSpace(label) ? KeyToString(KeyOf(entity)) : label;
    }

    // C# `required` members are compile-time only, and no entity declares a constructor, so the
    // parameterless one always exists; property initialisers (CreatedAt = DateTime.UtcNow) still run.
    public override object CreateInstance() =>
        Activator.CreateInstance(typeof(TEntity))
        ?? throw new InvalidOperationException($"Data Admin: could not construct {typeof(TEntity).Name}.");

    public override Task<long> CountAsync(ApplicationDbContext db, CancellationToken ct) =>
        db.Set<TEntity>().AsNoTracking().LongCountAsync(ct);

    public override async Task<object?> FindAsync(ApplicationDbContext db, object key, bool track, CancellationToken ct)
    {
        IQueryable<TEntity> query = track ? db.Set<TEntity>() : db.Set<TEntity>().AsNoTracking();
        return await query.FirstOrDefaultAsync(BuildEquals(Key, key), ct);
    }

    public override async Task<IReadOnlyDictionary<string, string>> LoadLabelsAsync(ApplicationDbContext db, IReadOnlyCollection<object> keys, CancellationToken ct)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chunk in keys.Chunk(DataAdminDefaults.KeyChunkSize))
        {
            var rows = await db.Set<TEntity>().AsNoTracking()
                .Where(BuildIn(Key, chunk))
                .Select(_labelProjection)
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                var keyText = KeyToString(row.Key);
                labels[keyText] = string.IsNullOrWhiteSpace(row.Label) ? keyText : row.Label;
            }
        }
        return labels;
    }

    public override Task<long> CountWhereAsync(ApplicationDbContext db, DataAdminColumn column, object value, CancellationToken ct) =>
        db.Set<TEntity>().AsNoTracking().Where(BuildEquals(column, value)).LongCountAsync(ct);

    public override async Task<long> CountReferencingAsync(ApplicationDbContext db, DataAdminColumn foreignKeyColumn, IReadOnlyList<object> principalKeys, CancellationToken ct)
    {
        long total = 0;
        foreach (var chunk in principalKeys.Chunk(DataAdminDefaults.KeyChunkSize))
        {
            total += await db.Set<TEntity>().AsNoTracking().Where(BuildIn(foreignKeyColumn, chunk)).LongCountAsync(ct);
        }
        return total;
    }

    public override async Task<IReadOnlyList<object>> KeysReferencingAsync(ApplicationDbContext db, DataAdminColumn foreignKeyColumn, IReadOnlyList<object> principalKeys, CancellationToken ct)
    {
        var keys = new List<object>();
        foreach (var chunk in principalKeys.Chunk(DataAdminDefaults.KeyChunkSize))
        {
            var found = await db.Set<TEntity>().AsNoTracking()
                .Where(BuildIn(foreignKeyColumn, chunk))
                .Select(_keySelector)
                .ToListAsync(ct);
            foreach (var key in found)
            {
                keys.Add(key);
            }
        }
        return keys;
    }

    // e => e.Column == @value, with the value parameterised (see DataAdminParameters).
    internal static Expression<Func<TEntity, bool>> BuildEquals(DataAdminColumn column, object value)
    {
        var member = Expression.Property(Row, column.Property);
        var parameter = DataAdminParameters.Value(value, column.ClrType);
        return Expression.Lambda<Func<TEntity, bool>>(Expression.Equal(member, parameter), Row);
    }

    // e => @keys.Contains(e.Column), nullable-safe: an int?/Guid? column becomes
    // e.Column.HasValue && @keys.Contains(e.Column.Value). Npgsql renders a parameterised list
    // as "= ANY(@keys)"; the InMemory provider evaluates it client-side.
    internal static Expression<Func<TEntity, bool>> BuildIn(DataAdminColumn column, IReadOnlyList<object> values)
    {
        var member = Expression.Property(Row, column.Property);
        var underlying = Nullable.GetUnderlyingType(column.ClrType);
        var elementType = underlying ?? column.ClrType;
        var list = DataAdminKeys.ToTypedList(elementType, values);
        var source = DataAdminParameters.Value(list, list.GetType());
        Expression target = underlying is null ? member : Expression.Property(member, nameof(Nullable<int>.Value));
        Expression contains = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), new[] { elementType }, source, target);
        Expression body = underlying is null
            ? contains
            : Expression.AndAlso(Expression.Property(member, nameof(Nullable<int>.HasValue)), contains);
        return Expression.Lambda<Func<TEntity, bool>>(body, Row);
    }

    public override async Task<DataAdminPageResult> QueryPageAsync(ApplicationDbContext db, DataAdminListQuery query, bool useILike, int pageSize, CancellationToken ct)
    {
        IQueryable<TEntity> rows = db.Set<TEntity>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search) && SearchColumns.Count > 0)
        {
            rows = rows.Where(BuildSearch(query.Search.Trim(), useILike));
        }
        foreach (var (name, raw) in query.Filters)
        {
            // Unknown columns and unparsable values are ignored, not errors: a stale bookmark
            // should still open the page.
            var column = FilterColumns.FirstOrDefault(c => c.Name == name);
            if (column is null || !DataAdminValues.TryConvert(raw, column, out var value, out _) || value is null)
            {
                continue;
            }
            rows = rows.Where(BuildEquals(column, value));
        }

        var total = await rows.LongCountAsync(ct);
        // An unknown sort column behaves like no sort at all: newest (highest key) first.
        var sortColumn = query.Sort is null ? null : DetailColumns.FirstOrDefault(c => c.Name == query.Sort);
        var descending = sortColumn is null || query.Descending;
        sortColumn ??= Key;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)Math.Max(1, pageSize)));
        var page = Math.Clamp(query.Page, 1, totalPages);

        var entities = await ApplySort(rows, sortColumn, descending)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return new DataAdminPageResult(entities.Cast<object>().ToList(), total, page, totalPages, sortColumn, descending);
    }

    // ORDER BY column [DESC], then by the key so paging is stable when the column repeats.
    private IQueryable<TEntity> ApplySort(IQueryable<TEntity> rows, DataAdminColumn column, bool descending)
    {
        var ordered = OrderBy(rows, column, descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy));
        return column.IsKey
            ? ordered
            : OrderBy(ordered, Key, descending ? nameof(Queryable.ThenByDescending) : nameof(Queryable.ThenBy));
    }

    // The same call tree Queryable.OrderBy<TSource, TKey> builds for itself, with the key type
    // taken from the column, so both providers see a perfectly ordinary OrderBy.
    private static IQueryable<TEntity> OrderBy(IQueryable<TEntity> rows, DataAdminColumn column, string method)
    {
        var body = Expression.Property(Row, column.Property);
        var lambda = Expression.Lambda(body, Row);
        return rows.Provider.CreateQuery<TEntity>(Expression.Call(
            typeof(Queryable), method, new[] { typeof(TEntity), body.Type }, rows.Expression, Expression.Quote(lambda)));
    }

    // e => ILIKE(e.Col1, @pattern) OR ILIKE(e.Col2, @pattern) ... on Npgsql (the tree the C#
    // compiler emits for EF.Functions.ILike), or e.Col1 != null && e.Col1.ToLower().Contains(@term)
    // where ILike cannot run (the InMemory provider evaluates client-side and ILike would throw).
    internal Expression<Func<TEntity, bool>> BuildSearch(string term, bool useILike)
    {
        var pattern = DataAdminParameters.Value("%" + DataAdminValues.EscapeLike(term) + "%", typeof(string));
        var lowered = DataAdminParameters.Value(term.ToLowerInvariant(), typeof(string));
        Expression? body = null;
        foreach (var column in SearchColumns)
        {
            var member = Expression.Property(Row, column.Property);
            Expression clause = useILike
                ? Expression.Call(ILikeMethod, EfFunctions, member, pattern)
                : Expression.AndAlso(
                    Expression.NotEqual(member, Expression.Constant(null, typeof(string))),
                    Expression.Call(Expression.Call(member, ToLowerMethod), ContainsMethod, lowered));
            body = body is null ? clause : Expression.OrElse(body, clause);
        }
        return Expression.Lambda<Func<TEntity, bool>>(body ?? Expression.Constant(false), Row);
    }

    private static readonly MethodInfo ILikeMethod =
        typeof(NpgsqlDbFunctionsExtensions).GetMethod(nameof(NpgsqlDbFunctionsExtensions.ILike), new[] { typeof(DbFunctions), typeof(string), typeof(string) })
        ?? throw new InvalidOperationException("EF.Functions.ILike(DbFunctions, string, string) not found.");

    private static readonly Expression EfFunctions =
        Expression.Property(null, typeof(EF).GetProperty(nameof(EF.Functions)) ?? throw new InvalidOperationException("EF.Functions not found."));

    private static readonly MethodInfo ToLowerMethod =
        typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes) ?? throw new InvalidOperationException("string.ToLower() not found.");

    private static readonly MethodInfo ContainsMethod =
        typeof(string).GetMethod(nameof(string.Contains), new[] { typeof(string) }) ?? throw new InvalidOperationException("string.Contains(string) not found.");

    private Expression LabelBody()
    {
        Expression? body = null;
        for (var i = LabelColumns.Count - 1; i >= 0; i--)
        {
            Expression member = Expression.Property(Row, LabelColumns[i].Property);
            body = body is null ? member : Expression.Coalesce(member, body);
        }
        return body ?? Expression.Constant(null, typeof(string));
    }
}
