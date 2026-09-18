using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LinkshellManagerDiscordApp.Services;

// One mapped scalar column of a Data Admin table, resolved once from EF metadata + policy.
public sealed class DataAdminColumn
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required Type ClrType { get; init; }
    // Nullable<T> unwrapped; equals ClrType for reference types and non-nullable value types.
    public required Type UnderlyingType { get; init; }
    public required bool IsNullable { get; init; }
    public int? MaxLength { get; init; }
    public required bool IsKey { get; init; }
    // False for keys the caller must supply (AppSetting.Key); true for identity/serial keys.
    public required bool IsGenerated { get; init; }
    // Never listed, never edited: credentials, Identity internals, blobs, jsonb arrays.
    public required bool IsProtected { get; init; }
    // Listed but never edited: derived values the policy recomputes (DkpPoolEventType.NormalizedEventType).
    public required bool IsReadOnly { get; init; }
    public required PropertyInfo Property { get; init; }
    public required IProperty Metadata { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    // The single-column foreign key this column carries, if any.
    public IForeignKey? ForeignKey { get; init; }
    // The principal table, when it is in the catalog (set by DataAdminCatalog after every model exists).
    public DataAdminModel? ForeignKeyTo { get; internal set; }

    public bool IsString => UnderlyingType == typeof(string);
    public bool IsBool => UnderlyingType == typeof(bool);
    public bool IsDateTime => UnderlyingType == typeof(DateTime) || UnderlyingType == typeof(DateTimeOffset);
}

// A table whose rows point at this one (the dependent side of a foreign key into this table).
public sealed class DataAdminReverseRelation
{
    public required DataAdminModel Dependent { get; init; }
    public required DataAdminColumn ForeignKeyColumn { get; init; }
    public required DeleteBehavior DeleteBehavior { get; init; }
}

// Projection shape for the label batch. A MemberInit of a plain class is the one projection
// every provider (Npgsql and the InMemory test provider) translates.
public sealed class DataAdminLabelRow<TKey>
{
    public TKey Key { get; set; } = default!;
    public string? Label { get; set; }
}

// Route ids are strings; keys are int, long, string or Guid. Everything key-type-specific is a
// switch on these four types so no reflection happens per request.
public static class DataAdminKeys
{
    public static bool IsSupported(Type keyType) =>
        keyType == typeof(int) || keyType == typeof(long) || keyType == typeof(string) || keyType == typeof(Guid);

    public static bool TryParse(Type keyType, string? raw, [NotNullWhen(true)] out object? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        if (keyType == typeof(int) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
        {
            key = i;
        }
        else if (keyType == typeof(long) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
        {
            key = l;
        }
        else if (keyType == typeof(Guid) && Guid.TryParse(raw, out var g))
        {
            key = g;
        }
        else if (keyType == typeof(string))
        {
            key = raw;
        }
        return key is not null;
    }

    // A concrete List<T> for a runtime element type, so Enumerable.Contains<T> binds to the exact
    // list type without MakeGenericMethod at query time.
    public static object ToTypedList(Type elementType, IEnumerable<object> values)
    {
        if (elementType == typeof(int))
        {
            return values.Select(value => (int)value).ToList();
        }
        if (elementType == typeof(long))
        {
            return values.Select(value => (long)value).ToList();
        }
        if (elementType == typeof(Guid))
        {
            return values.Select(value => (Guid)value).ToList();
        }
        if (elementType == typeof(string))
        {
            return values.Select(value => (string)value).ToList();
        }
        throw new InvalidOperationException($"Data Admin: key type {elementType.Name} is not supported.");
    }
}

// Values that must reach the database as PARAMETERS. EF Core 8 inlines a bare Expression.Constant
// into the SQL text and misses the compiled-query cache on every call (reference equality on the
// constant), so every value the engine puts into an expression tree goes through a holder object
// and a member access, which EF recognises as a closure and turns into a parameter.
public static class DataAdminParameters
{
    private sealed class Box
    {
        public object? Value;
        public Box(object? value) => Value = value;
    }

    public static Expression Value(object? value, Type type) =>
        Expression.Convert(Expression.Field(Expression.Constant(new Box(value)), nameof(Box.Value)), type);
}

// The non-generic surface of one Data Admin table: metadata, capabilities and the data operations
// the controller, the query service and the cascade walker call. The typed LINQ lives in the one
// generic subclass, DataAdminModel<TEntity, TKey>, which this factory closes at startup.
public abstract class DataAdminModel
{
    protected DataAdminModel(IEntityType entityType, DataAdminEntityPolicy policy)
    {
        EntityType = entityType;
        Policy = policy;
        ClrName = entityType.ClrType.Name;
        Slug = ToKebabCase(ClrName);
        DisplayName = Humanize(ClrName);
        TableName = entityType.GetTableName() ?? ClrName;

        var columns = new List<DataAdminColumn>();
        foreach (var property in entityType.GetProperties())
        {
            var info = property.PropertyInfo;
            if (info is null)
            {
                continue; // shadow property: nothing to read or bind
            }
            var clrType = property.ClrType;
            columns.Add(new DataAdminColumn
            {
                Name = property.Name,
                DisplayName = Humanize(property.Name),
                ClrType = clrType,
                UnderlyingType = Nullable.GetUnderlyingType(clrType) ?? clrType,
                IsNullable = property.IsNullable,
                MaxLength = property.GetMaxLength(),
                IsKey = property.IsPrimaryKey(),
                IsGenerated = property.ValueGenerated != ValueGenerated.Never,
                IsProtected = DataAdminPolicy.IsProtected(entityType.ClrType, info, policy),
                IsReadOnly = policy.ReadOnlyColumns.Contains(property.Name),
                Property = info,
                Metadata = property,
                Options = policy.Options.TryGetValue(property.Name, out var options) ? options : null,
                ForeignKey = property.GetContainingForeignKeys().FirstOrDefault(fk => fk.Properties.Count == 1),
            });
        }
        // Declaration order, so list pages read like the model file does.
        columns.Sort((a, b) => a.Property.MetadataToken.CompareTo(b.Property.MetadataToken));
        AllColumns = columns;

        var keys = columns.Where(column => column.IsKey).ToList();
        Key = keys.Count == 1
            ? keys[0]
            : throw new InvalidOperationException($"Data Admin: {ClrName} must have exactly one mapped key column (found {keys.Count}).");

        ValidatePolicyNames(policy, columns);

        var visible = columns.Where(column => !column.IsProtected).ToList();
        LabelColumns = ResolveLabelColumns(policy, visible);

        var list = new List<DataAdminColumn>(LabelColumns);
        if (!list.Contains(Key))
        {
            list.Add(Key);
        }
        foreach (var column in visible)
        {
            if (list.Count >= LabelColumns.Count + 1 + DataAdminDefaults.MaxListColumns)
            {
                break;
            }
            if (!list.Contains(column))
            {
                list.Add(column);
            }
        }
        ListColumns = list;
        DetailColumns = visible;
        SearchColumns = visible.Where(column => column.IsString).ToList();
        FilterColumns = visible.Where(column => column.IsKey || column.ForeignKey is not null).ToList();

        var editable = visible
            .Where(column => !column.IsKey && !column.IsReadOnly)
            .Where(column => policy.EditableAllowList is null || policy.EditableAllowList.Contains(column.Name))
            .ToList();
        EditableColumns = editable;
        // On create the caller supplies keys the database does not generate (AppSetting.Key).
        CreateColumns = visible
            .Where(column => editable.Contains(column) || (column.IsKey && !column.IsGenerated))
            .ToList();

        IsHidden = policy.Flags.HasFlag(DataAdminPolicyFlags.Hidden);
        CanCreate = !IsHidden && !policy.Flags.HasFlag(DataAdminPolicyFlags.NoCreate);
        CanEdit = !IsHidden && !policy.Flags.HasFlag(DataAdminPolicyFlags.NoEdit) && editable.Count > 0;
        CanDelete = !IsHidden && !policy.Flags.HasFlag(DataAdminPolicyFlags.NoDelete);
    }

    public IEntityType EntityType { get; }
    public DataAdminEntityPolicy Policy { get; }
    public Type ClrType => EntityType.ClrType;
    public string ClrName { get; }
    // URL segment: kebab-case of the CLR name (DkpPoolEventType -> dkp-pool-event-type).
    public string Slug { get; }
    public string DisplayName { get; }
    public string TableName { get; }
    public DataAdminColumn Key { get; }
    public IReadOnlyList<DataAdminColumn> AllColumns { get; }
    public IReadOnlyList<DataAdminColumn> LabelColumns { get; }
    public IReadOnlyList<DataAdminColumn> ListColumns { get; }
    public IReadOnlyList<DataAdminColumn> DetailColumns { get; }
    public IReadOnlyList<DataAdminColumn> SearchColumns { get; }
    public IReadOnlyList<DataAdminColumn> FilterColumns { get; }
    public IReadOnlyList<DataAdminColumn> EditableColumns { get; }
    public IReadOnlyList<DataAdminColumn> CreateColumns { get; }
    public IReadOnlyList<DataAdminReverseRelation> ReverseRelations { get; private set; } = Array.Empty<DataAdminReverseRelation>();
    public bool IsHidden { get; }
    public bool CanCreate { get; }
    public bool CanEdit { get; }
    public bool CanDelete { get; }
    public bool IsReadOnly => !CanCreate && !CanEdit && !CanDelete;

    // Tables whose deletion cascades into this one (other than itself), once relations are linked.
    public IReadOnlyList<DataAdminModel> CascadePrincipals => AllColumns
        .Where(column => column.ForeignKey is { DeleteBehavior: DeleteBehavior.Cascade } && column.ForeignKeyTo is not null && !ReferenceEquals(column.ForeignKeyTo, this))
        .Select(column => column.ForeignKeyTo!)
        .Distinct()
        .ToList();

    public DataAdminColumn? FindColumn(string name) =>
        AllColumns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.Ordinal));

    public abstract Type KeyType { get; }
    public abstract bool TryParseKey(string? raw, [NotNullWhen(true)] out object? key);
    public abstract string KeyToString(object key);
    public abstract object KeyOf(object entity);
    public abstract string LabelOf(object entity);
    public abstract object CreateInstance();

    public abstract Task<long> CountAsync(ApplicationDbContext db, CancellationToken ct);
    public abstract Task<object?> FindAsync(ApplicationDbContext db, object key, bool track, CancellationToken ct);
    // Key text -> label for every row whose key is in `keys`; rows without a label fall back to the key.
    public abstract Task<IReadOnlyDictionary<string, string>> LoadLabelsAsync(ApplicationDbContext db, IReadOnlyCollection<object> keys, CancellationToken ct);
    public abstract Task<long> CountWhereAsync(ApplicationDbContext db, DataAdminColumn column, object value, CancellationToken ct);
    public abstract Task<long> CountReferencingAsync(ApplicationDbContext db, DataAdminColumn foreignKeyColumn, IReadOnlyList<object> principalKeys, CancellationToken ct);
    public abstract Task<IReadOnlyList<object>> KeysReferencingAsync(ApplicationDbContext db, DataAdminColumn foreignKeyColumn, IReadOnlyList<object> principalKeys, CancellationToken ct);

    // Called by the catalog once every model exists: FK targets and referencing tables are only
    // meaningful when both ends are catalog tables (Identity's claim/login/token tables are not).
    internal void LinkRelations(DataAdminCatalog catalog)
    {
        foreach (var column in AllColumns)
        {
            if (column.ForeignKey is not null)
            {
                column.ForeignKeyTo = catalog.FindByClrType(column.ForeignKey.PrincipalEntityType.ClrType);
            }
        }

        var reverse = new List<DataAdminReverseRelation>();
        foreach (var fk in EntityType.GetReferencingForeignKeys())
        {
            if (fk.Properties.Count != 1)
            {
                continue;
            }
            var dependent = catalog.FindByClrType(fk.DeclaringEntityType.ClrType);
            var column = dependent?.FindColumn(fk.Properties[0].Name);
            if (dependent is null || column is null)
            {
                continue;
            }
            reverse.Add(new DataAdminReverseRelation { Dependent = dependent, ForeignKeyColumn = column, DeleteBehavior = fk.DeleteBehavior });
        }
        ReverseRelations = reverse
            .OrderBy(relation => relation.Dependent.DisplayName, StringComparer.Ordinal)
            .ThenBy(relation => relation.ForeignKeyColumn.Name, StringComparer.Ordinal)
            .ToList();
    }

    // Closes DataAdminModel<TEntity, TKey> over the entity and its key type. Reflection happens
    // here, once per table at startup; a policy error inside the constructor is rethrown unwrapped
    // so the message names the table and column instead of a TargetInvocationException.
    public static DataAdminModel Create(IEntityType entityType, DataAdminEntityPolicy policy)
    {
        var key = entityType.FindPrimaryKey()
            ?? throw new InvalidOperationException($"Data Admin: {entityType.DisplayName()} has no primary key.");
        if (key.Properties.Count != 1)
        {
            throw new InvalidOperationException($"Data Admin: {entityType.DisplayName()} has a composite key, which is not supported.");
        }
        var keyType = key.Properties[0].ClrType;
        if (!DataAdminKeys.IsSupported(keyType))
        {
            throw new InvalidOperationException($"Data Admin: key type {keyType.Name} on {entityType.DisplayName()} is not supported.");
        }

        var closed = typeof(DataAdminModel<,>).MakeGenericType(entityType.ClrType, keyType);
        try
        {
            return Activator.CreateInstance(closed, entityType, policy) as DataAdminModel
                ?? throw new InvalidOperationException($"Data Admin: could not construct {closed.Name}.");
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw();
            throw;
        }
    }

    private IReadOnlyList<DataAdminColumn> ResolveLabelColumns(DataAdminEntityPolicy policy, IReadOnlyList<DataAdminColumn> visible)
    {
        if (policy.LabelColumns is { Count: > 0 } names)
        {
            return names.Select(name => visible.FirstOrDefault(column => column.Name == name && column.IsString)
                    ?? throw new InvalidOperationException($"Data Admin: label column '{name}' is not a visible string column on {ClrName}."))
                .ToList();
        }
        foreach (var guess in DataAdminDefaults.LabelGuessOrder)
        {
            var match = visible.FirstOrDefault(column => column.Name == guess && column.IsString);
            if (match is not null)
            {
                return new[] { match };
            }
        }
        return Array.Empty<DataAdminColumn>();
    }

    // Every column name the policy mentions must exist, so a typo fails at startup (and in
    // DataAdminCatalogTests) instead of silently exposing or hiding the wrong thing.
    private void ValidatePolicyNames(DataAdminEntityPolicy policy, IReadOnlyList<DataAdminColumn> columns)
    {
        var known = columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        void Check(IEnumerable<string>? names, string what)
        {
            foreach (var name in names ?? Array.Empty<string>())
            {
                if (!known.Contains(name))
                {
                    throw new InvalidOperationException($"Data Admin policy: {ClrName} has no column '{name}' ({what}).");
                }
            }
        }
        Check(policy.EditableAllowList, "EditableAllowList");
        Check(policy.ProtectedColumns, "ProtectedColumns");
        Check(policy.ReadOnlyColumns, "ReadOnlyColumns");
        Check(policy.Options.Keys, "Options");
        Check(policy.SyncedCopies.Select(copy => copy.TargetColumn), "SyncedCopies.TargetColumn");
        Check(policy.SyncedCopies.Select(copy => copy.ViaForeignKey), "SyncedCopies.ViaForeignKey");
    }

    private static string ToKebabCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
            {
                builder.Append('-');
            }
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static string Humanize(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && !char.IsUpper(name[i - 1]))
            {
                builder.Append(' ');
            }
            builder.Append(c);
        }
        return builder.ToString();
    }
}
