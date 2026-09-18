using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
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
