using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace LinkshellManagerDiscordApp.Services;

// One column on a table card. Type is the short text the card shows (varchar(32), timestamptz);
// StoreType is the database's own spelling, for the tooltip. Protected marks a column whose VALUES
// Data Admin never shows (credentials, blobs): its name and type are structure, so it is still drawn.
public sealed record DataAdminSchemaColumn(
    string Name, string Type, string StoreType, bool Pk, bool Fk, bool Unique, bool Nullable, bool Protected);

// A unique index. Filter is the model's own text for a partial index, null for a plain one.
public sealed record DataAdminSchemaUnique(IReadOnlyList<string> Columns, string? Filter);

// One table. Id is the CLR type name, never the table name: it is the same on every provider and
// it is what the page keys its state on (selection, moved cards). Slug is set only where
// /data-admin can route to the rows; Framework marks the Identity tables, which are drawn but
// never browsable. Hub and Parent are layout hints and nothing else.
public sealed record DataAdminSchemaTable(
    string Id, string Name, string Label, string? Slug, bool Framework, bool Hub, string? Parent,
    IReadOnlyList<DataAdminSchemaColumn> Columns, IReadOnlyList<DataAdminSchemaUnique> Uniques);

// One foreign key, from the dependent table (From) to the principal (To). Required and Unique decide
// the two line ends. OnDelete is what Postgres does to the dependent rows, in the delete preview's
// three outcomes (cascade, setNull, restrict); EfDelete is EF's own name for it, for the tooltip.
public sealed record DataAdminSchemaRelationship(
    string Id, string From, IReadOnlyList<string> FromColumns, string To, IReadOnlyList<string> ToColumns,
    bool Required, bool Unique, string OnDelete, string EfDelete);

// A many-to-many the database itself enforces: Via holds at most one row per (Left, Right) pair.
public sealed record DataAdminSchemaJunction(
    string Via, string Left, string Right, string LeftRelationship, string RightRelationship);

// The whole database as a picture, for the Data Admin schema diagram: every table, column, unique
// index and foreign key in the EF model. Structure only -- no row is ever read. Built from EF
// metadata so it cannot go stale: a new entity or foreign key appears on the diagram by itself.
//
// It does not walk the model. DataAdminCatalog (the one place allowed to) hands it the catalog
// tables plus the tables it skipped, and builds it lazily so a mistake in here can break the
// schema page but never startup.
//
// Only metadata that reads the same on Npgsql and on the InMemory test provider is used. Two
// things do differ and are handled on purpose: the store type exists on a relational provider
// only (the InMemory model gets a type guessed from the CLR type), and so does the DbSet table
// name, which is why nothing is keyed by table name.
public sealed class DataAdminSchemaGraph
{
    private const string OnDeleteCascade = "cascade";
    private const string OnDeleteSetNull = "setNull";
    private const string OnDeleteRestrict = "restrict";

    // The graph sits inside a <script type="application/json"> block, so a name carrying
    // "</script>" would end the block early. Nothing in the model does, but the relaxed encoder
    // would let one through unescaped; this one escapes < > & and quotes (same choice as
    // ChartBoardViewModel.ItemOptionsJson). False flags and nulls are left out to keep the block
    // small: the script reads a missing flag as false.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Default,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private DataAdminSchemaGraph(
        IReadOnlyList<DataAdminSchemaTable> tables,
        IReadOnlyList<DataAdminSchemaRelationship> relationships,
        IReadOnlyList<DataAdminSchemaJunction> junctions)
    {
        Tables = tables;
        Relationships = relationships;
        Junctions = junctions;
        ColumnCount = tables.Sum(table => table.Columns.Count);
        Json = JsonSerializer.Serialize(new { tables, relationships, junctions }, JsonOptions);
    }

    // Tables and relationships are ordered by id (ordinal) and junctions by Via, Left, Right, so
    // neither the page nor a test ever depends on the order EF happens to enumerate the model in.
    public IReadOnlyList<DataAdminSchemaTable> Tables { get; }
    public IReadOnlyList<DataAdminSchemaRelationship> Relationships { get; }
    public IReadOnlyList<DataAdminSchemaJunction> Junctions { get; }
    public int ColumnCount { get; }
    // Serialized once: the model never changes while the process runs, so every request reuses it.
    public string Json { get; }

    // A table before its lines are known. Catalog tables carry their DataAdminModel; the
    // structure-only ones (Identity) have none.
    private sealed record Source(string Id, IEntityType EntityType, DataAdminModel? Model);

    internal static DataAdminSchemaGraph Build(IReadOnlyList<DataAdminModel> models, IReadOnlyList<IEntityType> structureOnly)
    {
        var sources = models.Select(model => new Source(model.ClrName, model.EntityType, model))
            .Concat(structureOnly.Select(entityType => new Source(TableId(entityType), entityType, null)))
            .OrderBy(source => source.Id, StringComparer.Ordinal)
            .ToList();
        // The id names a card, its saved position and both ends of every line, so two tables can
        // never share one. Sorted, so a clash is always two neighbours.
        for (var i = 1; i < sources.Count; i++)
        {
            if (sources[i].Id == sources[i - 1].Id)
            {
                throw new InvalidOperationException(
                    $"Data Admin schema: {sources[i - 1].EntityType.Name} and {sources[i].EntityType.Name} would both be drawn as '{sources[i].Id}'.");
            }
        }
        var idOf = sources.ToDictionary(source => source.EntityType, source => source.Id);

        var tables = new List<DataAdminSchemaTable>();
        var relationships = new List<DataAdminSchemaRelationship>();
        var junctions = new List<DataAdminSchemaJunction>();
        var relationshipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var keyProperties = source.EntityType.FindPrimaryKey()?.Properties ?? Array.Empty<IProperty>();
            var uniqueIndexes = source.EntityType.GetIndexes().Where(index => index.IsUnique).ToList();
            // The column sets that can hold a value only once in the table: unique indexes minus
            // the partial ones.
            var plainUnique = uniqueIndexes
                .Where(index => IsPlainUniqueFilter(index.GetFilter(), ColumnNames(index.Properties)))
                .Select(index => index.Properties)
                .ToList();

            tables.Add(new DataAdminSchemaTable(
                Id: source.Id,
                Name: source.EntityType.GetTableName() ?? source.Id,
                Label: source.Model?.DisplayName ?? DataAdminModel.Humanize(source.Id),
                Slug: source.Model is { IsHidden: false } routable ? routable.Slug : null,
                Framework: source.Model is null,
                Hub: false,
                Parent: null,
                Columns: BuildColumns(source, keyProperties, plainUnique),
                Uniques: uniqueIndexes
                    .Select(index => new DataAdminSchemaUnique(ColumnNames(index.Properties), index.GetFilter()))
                    .ToList()));

            var outgoing = BuildRelationships(source, idOf, keyProperties, plainUnique, relationshipIds);
            relationships.AddRange(outgoing);
            junctions.AddRange(FindJunctions(source.Id, keyProperties, plainUnique, outgoing));
        }

        // A self-reference says nothing about how central a table is, so it is not counted.
        var inbound = relationships
            .Where(relationship => relationship.From != relationship.To)
            .GroupBy(relationship => relationship.To, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        bool IsHub(string id) => inbound.TryGetValue(id, out var count) && count >= DataAdminDefaults.SchemaHubMinInbound;

        var forest = BuildLayoutForest(sources.Select(source => source.Id).ToList(), relationships, IsHub);

        return new DataAdminSchemaGraph(
            tables.Select(table => table with { Hub = IsHub(table.Id), Parent = forest.ByItem[table.Id].Parent?.Item }).ToList(),
            relationships.OrderBy(relationship => relationship.Id, StringComparer.Ordinal).ToList(),
            junctions
                .OrderBy(junction => junction.Via, StringComparer.Ordinal)
                .ThenBy(junction => junction.Left, StringComparer.Ordinal)
                .ThenBy(junction => junction.Right, StringComparer.Ordinal)
                .ToList());
    }

    // The short type a card shows. Postgres spells its common types at length ("character
    // varying(32)", "timestamp with time zone"), which does not fit beside a column name, so those
    // are shortened to the aliases Postgres itself accepts. Without a store type (the InMemory test
    // provider has none) the type is guessed from the CLR type the way Npgsql maps it here; every
    // array property in this app is a jsonb column (see DataAdminPolicy.IsProtected).
    internal static string DisplayType(string? storeType, Type clrType, int? maxLength)
    {
        if (storeType is not null)
        {
            const string varying = "character varying";
            if (storeType.StartsWith(varying + "(", StringComparison.Ordinal))
            {
                return "varchar" + storeType[varying.Length..];
            }
            return storeType switch
            {
                varying => "varchar",
                "timestamp with time zone" => "timestamptz",
                "timestamp without time zone" => "timestamp",
                "double precision" => "float8",
                "integer" => "int",
                "boolean" => "bool",
                _ => storeType,
            };
        }

        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (type == typeof(string))
        {
            return maxLength is { } length ? "varchar(" + length.ToString(CultureInfo.InvariantCulture) + ")" : "text";
        }
        if (type == typeof(int))
        {
            return "int";
        }
        if (type == typeof(long))
        {
            return "bigint";
        }
        if (type == typeof(short))
        {
            return "smallint";
        }
        if (type == typeof(bool))
        {
            return "bool";
        }
        if (type == typeof(double))
        {
            return "float8";
        }
        if (type == typeof(float))
        {
            return "float4";
        }
        if (type == typeof(decimal))
        {
            return "numeric";
        }
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return "timestamptz";
        }
        if (type == typeof(Guid))
        {
            return "uuid";
        }
        if (type == typeof(byte[]))
        {
            return "bytea";
        }
        if (type.IsArray)
        {
            return "jsonb";
        }
        if (type.IsEnum)
        {
            return "int";
        }
        return type.Name.ToLowerInvariant();
    }

    // Whether a unique index still means "a value can appear only once". An unfiltered index does,
    // and so does one filtered only by "<its own column>" IS NOT NULL: the rows it leaves out have
    // no value to repeat. Any other filter ("IsDefault" = TRUE) makes the index a rule about SOME
    // rows, and the column must not be drawn as unique. Text this does not recognise counts as
    // such a filter, so the diagram never claims a "one" the database does not enforce.
    internal static bool IsPlainUniqueFilter(string? filter, IReadOnlyList<string> indexColumns)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }
        const string isNotNull = " IS NOT NULL";
        var text = filter.Trim();
        return indexColumns.Any(column =>
            text.Length == column.Length + 2 + isNotNull.Length
            && text.StartsWith('"' + column + '"', StringComparison.Ordinal)
            && text.EndsWith(isNotNull, StringComparison.OrdinalIgnoreCase));
    }

    // Primary key first, then the order the row pages use (declaration order), then whatever the
    // catalog model does not carry (shadow properties; every column of an Identity table) by name.
    private static List<DataAdminSchemaColumn> BuildColumns(
        Source source, IReadOnlyList<IProperty> keyProperties, IReadOnlyList<IReadOnlyList<IProperty>> plainUnique)
    {
        var ordered = new List<IProperty>();
        var taken = new HashSet<IProperty>();
        void Take(IEnumerable<IProperty> properties)
        {
            foreach (var property in properties)
            {
                if (taken.Add(property))
                {
                    ordered.Add(property);
                }
            }
        }
        Take(keyProperties);
        Take(source.Model?.AllColumns.Select(column => column.Metadata) ?? Enumerable.Empty<IProperty>());
        Take(source.EntityType.GetProperties().OrderBy(property => property.Name, StringComparer.Ordinal));

        // A lone primary key column already carries the PK badge, which says "unique" by itself.
        var soleKey = keyProperties.Count == 1 ? keyProperties[0] : null;
        var uniqueAlone = plainUnique.Where(set => set.Count == 1).Select(set => set[0]).ToHashSet();

        var columns = new List<DataAdminSchemaColumn>(ordered.Count);
        foreach (var property in ordered)
        {
            var storeType = (property.FindTypeMapping() as RelationalTypeMapping)?.StoreType;
            var type = DisplayType(storeType, property.ClrType, property.GetMaxLength());
            columns.Add(new DataAdminSchemaColumn(
                Name: property.GetColumnName(),
                Type: type,
                StoreType: storeType ?? type,
                Pk: property.IsPrimaryKey(),
                Fk: property.GetContainingForeignKeys().Any(),
                Unique: uniqueAlone.Contains(property) && !ReferenceEquals(property, soleKey),
                Nullable: property.IsNullable,
                Protected: source.Model?.FindColumn(property.Name)?.IsProtected ?? false));
        }
        return columns;
    }

    // One relationship per foreign key that leaves this table for a table on the diagram.
    private static List<DataAdminSchemaRelationship> BuildRelationships(
        Source source,
        IReadOnlyDictionary<IEntityType, string> idOf,
        IReadOnlyList<IProperty> keyProperties,
        IReadOnlyList<IReadOnlyList<IProperty>> plainUnique,
        HashSet<string> takenIds)
    {
        var relationships = new List<DataAdminSchemaRelationship>();
        foreach (var foreignKey in source.EntityType.GetForeignKeys())
        {
            if (!idOf.TryGetValue(foreignKey.PrincipalEntityType, out var to))
            {
                continue;
            }
            var fromColumns = ColumnNames(foreignKey.Properties);
            // Table plus column reads best and is unique in practice; the same columns can only
            // come back when they also point at a second table, which then names itself.
            var id = $"{source.Id}.{string.Join('+', fromColumns)}";
            if (!takenIds.Add(id))
            {
                id = $"{id}>{to}";
                takenIds.Add(id);
            }
            relationships.Add(new DataAdminSchemaRelationship(
                Id: id,
                From: source.Id,
                FromColumns: fromColumns,
                To: to,
                ToColumns: ColumnNames(foreignKey.PrincipalKey.Properties),
                Required: foreignKey.IsRequired,
                // At most one dependent row per principal row: EF says so (a one-to-one), or the
                // foreign key is the whole primary key, or a unique index sits exactly on it.
                Unique: foreignKey.IsUnique
                    || SameProperties(foreignKey.Properties, keyProperties)
                    || plainUnique.Any(set => SameProperties(set, foreignKey.Properties)),
                OnDelete: DataAdminCascadePreview.Classify(foreignKey.DeleteBehavior) switch
                {
                    DataAdminCascadeImpact.Effect.Deleted => OnDeleteCascade,
                    DataAdminCascadeImpact.Effect.Unlinked => OnDeleteSetNull,
                    _ => OnDeleteRestrict,
                },
                EfDelete: foreignKey.DeleteBehavior.ToString()));
        }
        return relationships;
    }

    // Strict many-to-many: a two-column key (the primary key or a unique index) whose columns are
    // each a foreign key to a different other table. A table that merely has two foreign keys
    // (AppUserLinkshell) is not one: nothing in the database stops a pair from repeating.
    private static List<DataAdminSchemaJunction> FindJunctions(
        string via,
        IReadOnlyList<IProperty> keyProperties,
        IReadOnlyList<IReadOnlyList<IProperty>> plainUnique,
        IReadOnlyList<DataAdminSchemaRelationship> outgoing)
    {
        var pairs = plainUnique.Where(set => set.Count == 2).ToList();
        if (keyProperties.Count == 2)
        {
            pairs.Insert(0, keyProperties);
        }

        var junctions = new List<DataAdminSchemaJunction>();
        foreach (var pair in pairs)
        {
            foreach (var first in outgoing.Where(relationship => IsOnlyColumn(relationship, pair[0])))
            {
                foreach (var second in outgoing.Where(relationship => IsOnlyColumn(relationship, pair[1])))
                {
                    if (first.To == second.To || first.To == via || second.To == via)
                    {
                        continue;
                    }
                    var (left, right) = string.CompareOrdinal(first.To, second.To) < 0 ? (first, second) : (second, first);
                    // The primary key and a unique index can cover the same pair; list it once.
                    if (!junctions.Any(junction => junction.Left == left.To && junction.Right == right.To))
                    {
                        junctions.Add(new DataAdminSchemaJunction(via, left.To, right.To, left.Id, right.Id));
                    }
                }
            }
        }
        return junctions;
    }

    // Layout hint only: a table is placed beside the table that owns its rows, by the same rule as
    // the catalog's cascade forest (cascade delete, deepest owner wins). A table nothing cascades
    // into would float on its own, so it falls back to whatever it points at -- except a hub, or
    // Linkshell would nest under AppUser through Linkshell.AppUserId and take the diagram with it.
    private static DataAdminCascadeForest<string> BuildLayoutForest(
        IReadOnlyList<string> ids, IReadOnlyList<DataAdminSchemaRelationship> relationships, Func<string, bool> isHub)
    {
        var edges = new List<(string Dependent, string Principal)>();
        var outgoing = relationships
            .Where(relationship => relationship.From != relationship.To)
            .GroupBy(relationship => relationship.From, StringComparer.Ordinal);
        foreach (var table in outgoing)
        {
            var owners = table.Where(relationship => relationship.OnDelete == OnDeleteCascade).ToList();
            if (owners.Count == 0 && !isHub(table.Key))
            {
                owners = table.ToList();
            }
            edges.AddRange(owners.Select(relationship => (relationship.From, relationship.To)));
        }
        return DataAdminCascadeForest<string>.Build(ids, edges, id => id, id => id == nameof(Linkshell));
    }

    // IdentityUserRole<string> is IdentityUserRole`1 to reflection; the arity means nothing on a
    // card. A shared-type entity has no CLR name of its own, so the last part of its EF name stands in.
    private static string TableId(IEntityType entityType)
    {
        if (entityType.HasSharedClrType)
        {
            return entityType.Name[(entityType.Name.LastIndexOf('.') + 1)..];
        }
        var name = entityType.ClrType.Name;
        var arity = name.IndexOf('`');
        return arity < 0 ? name : name[..arity];
    }

    private static List<string> ColumnNames(IReadOnlyList<IProperty> properties) =>
        properties.Select(property => property.GetColumnName()).ToList();

    private static bool SameProperties(IReadOnlyList<IProperty> left, IReadOnlyList<IProperty> right) =>
        left.Count == right.Count && left.All(property => right.Contains(property));

    private static bool IsOnlyColumn(DataAdminSchemaRelationship relationship, IProperty property) =>
        relationship.FromColumns.Count == 1 && relationship.FromColumns[0] == property.GetColumnName();
}
