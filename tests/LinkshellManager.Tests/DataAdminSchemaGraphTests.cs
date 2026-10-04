using System.Text.Json;
using System.Text.RegularExpressions;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LinkshellManager.Tests;

// The schema diagram draws the WHOLE database from EF metadata, so the thing to pin is that the
// picture tells the truth about the real model: every table and foreign key is on it (the six
// Identity tables included, drawn but never a route to rows), credential columns are drawn and
// flagged, a "one" line end appears only where the database enforces it for every row (DkpPool's
// "one default pool per linkshell" is a partial index and must not count), and each line carries
// the delete rule Postgres will apply. Ids are CLR names because table names differ on the test
// provider, and no total is pinned, so adding an entity never breaks these tests. One test builds
// the model with Npgsql WITHOUT connecting: the only way to reach the production path (DbSet table
// names, real store types) from a unit test. The Synthetic_ tests cover shapes the real model does
// not have today: a two-column foreign key, one column pointing at two tables, two tables that
// would share an id.
public class DataAdminSchemaGraphTests
{
    private const string HostileTableName = "</script><script>alert(1)</script>";

    private static readonly string[] IdentityTables =
    {
        "IdentityRole", "IdentityRoleClaim", "IdentityUserClaim", "IdentityUserLogin", "IdentityUserRole", "IdentityUserToken",
    };

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DataAdminSchemaGraph NewGraph()
    {
        using var db = NewInMemoryContext();
        return new DataAdminCatalog(db.Model, DataAdminPolicy.Default).Schema;
    }

    private static OddShapesContext NewOddShapesContext() =>
        new(new DbContextOptionsBuilder<OddShapesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DataAdminSchemaGraph NewOddShapesGraph()
    {
        using var db = NewOddShapesContext();
        return new DataAdminCatalog(db.Model, DataAdminPolicy.Default).Schema;
    }

    private static DataAdminSchemaTable Table(DataAdminSchemaGraph graph, string id) =>
        graph.Tables.FirstOrDefault(table => table.Id == id)
        ?? throw new InvalidOperationException($"{id} is not in the graph.");

    private static DataAdminSchemaColumn Column(DataAdminSchemaGraph graph, string table, string column) =>
        Table(graph, table).Columns.FirstOrDefault(candidate => candidate.Name == column)
        ?? throw new InvalidOperationException($"{table}.{column} is not in the graph.");

    private static DataAdminSchemaRelationship Relationship(DataAdminSchemaGraph graph, string id) =>
        graph.Relationships.FirstOrDefault(relationship => relationship.Id == id)
        ?? throw new InvalidOperationException($"{id} is not in the graph.");

    // What the graph calls an entity type: its CLR name without the generic arity.
    private static string IdOf(IEntityType entityType) => entityType.ClrType.Name.Split('`')[0];

    private static IEntityType EntityType(DbContext db, Type clrType) =>
        db.Model.FindEntityType(clrType) ?? throw new InvalidOperationException($"{clrType.Name} is not in the model.");

    // Every property in a JSON document, at any depth.
    private static IEnumerable<JsonProperty> Properties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(property => Properties(property.Value).Prepend(property)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Properties),
        _ => Enumerable.Empty<JsonProperty>(),
    };

    // ---- tables ----

    [Fact]
    public void EveryTable_IsDrawn_CatalogTablesWithTheirSlug_IdentityTablesWithout()
    {
        using var db = NewInMemoryContext();
        var catalog = new DataAdminCatalog(db.Model, DataAdminPolicy.Default);
        var graph = catalog.Schema;

        foreach (var model in catalog.Models)
        {
            var table = Table(graph, model.ClrName);
            Assert.Equal(model.Slug, table.Slug);
            Assert.Equal(model.DisplayName, table.Label);
            Assert.False(table.Framework, $"{table.Id} is a catalog table.");
        }
        foreach (var id in IdentityTables)
        {
            var table = Table(graph, id);
            Assert.True(table.Framework, $"{id} is an Identity framework table.");
            Assert.Null(table.Slug);
        }
        Assert.Equal("Identity User Role", Table(graph, "IdentityUserRole").Label);
        Assert.Equal("app-user", Table(graph, nameof(AppUser)).Slug);

        // Nothing else is on the diagram: one table per entity type, each either a catalog table
        // or structure only.
        Assert.All(graph.Tables, table => Assert.Equal(catalog.FindByClrName(table.Id) is null, table.Framework));
        Assert.Equal(db.Model.GetEntityTypes().Count(entityType => !entityType.IsOwned()), graph.Tables.Count);
        // Do not pin the exact number; this only proves the graph walked the real model.
        Assert.True(graph.Tables.Count > 50, $"Expected the full model, got {graph.Tables.Count} tables.");
    }

    // The id names a card, a saved position and both ends of every line, and the page keeps its
    // elements in maps keyed by it.
    [Fact]
    public void Ids_AreUnique_Plain_AndInOrdinalOrder()
    {
        var graph = NewGraph();
        var tableIds = graph.Tables.Select(table => table.Id).ToList();
        var relationshipIds = graph.Relationships.Select(relationship => relationship.Id).ToList();

        Assert.Equal(tableIds.Count, tableIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tableIds, id => Assert.Matches(new Regex("^[A-Za-z][A-Za-z0-9_]*$"), id));
        Assert.Equal(tableIds.OrderBy(id => id, StringComparer.Ordinal), tableIds);

        Assert.Equal(relationshipIds.Count, relationshipIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(relationshipIds.OrderBy(id => id, StringComparer.Ordinal), relationshipIds);
    }

    [Fact]
    public void Schema_IsBuiltOnce()
    {
        using var db = NewInMemoryContext();
        var catalog = new DataAdminCatalog(db.Model, DataAdminPolicy.Default);

        Assert.Same(catalog.Schema, catalog.Schema);
        Assert.Same(catalog.Schema.Json, catalog.Schema.Json);
    }

    // Hidden tables can never be routed to, so the diagram must not offer a way to their rows.
    [Fact]
    public void HiddenTable_IsStillDrawn_ButHasNoSlug()
    {
        var policy = new DataAdminPolicy(new Dictionary<Type, DataAdminEntityPolicy>
        {
            [typeof(Rule)] = new() { Flags = DataAdminPolicyFlags.Hidden },
        });
        using var db = NewInMemoryContext();

        var rule = Table(new DataAdminCatalog(db.Model, policy).Schema, nameof(Rule));

        Assert.Null(rule.Slug);
        Assert.False(rule.Framework);
        Assert.NotEmpty(rule.Columns);
    }

    // ---- columns ----

    [Fact]
    public void EveryTable_ListsEveryMappedColumn_PrimaryKeyFirst()
    {
        using var db = NewInMemoryContext();
        var graph = new DataAdminCatalog(db.Model, DataAdminPolicy.Default).Schema;

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var table = Table(graph, IdOf(entityType));
            var columns = table.Columns.Select(column => column.Name).ToList();

            Assert.Equal(entityType.GetProperties().Count(), columns.Count);
            Assert.Equal(
                entityType.GetProperties().Select(property => property.GetColumnName()).OrderBy(name => name, StringComparer.Ordinal),
                columns.OrderBy(name => name, StringComparer.Ordinal));

            var key = entityType.FindPrimaryKey()?.Properties.Select(property => property.GetColumnName()).ToList() ?? new List<string>();
            Assert.Equal(key, columns.Take(key.Count));
            Assert.All(table.Columns.Take(key.Count), column => Assert.True(column.Pk, $"{table.Id}.{column.Name}"));
            Assert.All(table.Columns.Skip(key.Count), column => Assert.False(column.Pk, $"{table.Id}.{column.Name}"));
        }
        Assert.Equal(graph.Tables.Sum(table => table.Columns.Count), graph.ColumnCount);
    }

    // After the key, a catalog table reads like its row pages (declaration order); an Identity
    // table has no catalog model, so its columns follow by name.
    [Fact]
    public void Columns_FollowTheCatalogOrder_OrTheNameWhereThereIsNoCatalogModel()
    {
        using var db = NewInMemoryContext();
        var catalog = new DataAdminCatalog(db.Model, DataAdminPolicy.Default);
        var graph = catalog.Schema;

        foreach (var model in catalog.Models)
        {
            Assert.Equal(
                model.AllColumns.Where(column => !column.IsKey).Select(column => column.Name),
                Table(graph, model.ClrName).Columns.Where(column => !column.Pk).Select(column => column.Name));
        }
        Assert.Equal(
            new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
            Table(graph, "IdentityRole").Columns.Select(column => column.Name));
        // Key order (UserId, LoginProvider, Name), not name order.
        Assert.Equal(
            new[] { "UserId", "LoginProvider", "Name", "Value" },
            Table(graph, "IdentityUserToken").Columns.Select(column => column.Name));
    }

    // A credential column is structure, so it is on the diagram; the flag is what tells the page
    // its value is never shown anywhere in Data Admin.
    [Fact]
    public void CredentialColumns_AreDrawn_AndFlaggedProtected()
    {
        using var db = NewInMemoryContext();
        var catalog = new DataAdminCatalog(db.Model, DataAdminPolicy.Default);
        var graph = catalog.Schema;

        Assert.True(Column(graph, nameof(AppUser), nameof(AppUser.PasswordHash)).Protected);
        Assert.True(Column(graph, nameof(AddonApiToken), nameof(AddonApiToken.TokenHash)).Protected);
        Assert.False(Column(graph, nameof(AppUser), nameof(AppUser.CharacterName)).Protected);

        // The flag is the catalog's own rule, column for column; structure-only tables have none.
        foreach (var model in catalog.Models)
        {
            foreach (var column in model.AllColumns)
            {
                Assert.Equal(column.IsProtected, Column(graph, model.ClrName, column.Name).Protected);
            }
        }
        Assert.All(
            graph.Tables.Where(table => table.Framework).SelectMany(table => table.Columns),
            column => Assert.False(column.Protected));
    }

    [Fact]
    public void KeyFlags_ComeFromMetadata()
    {
        var graph = NewGraph();

        // One-to-one on a shared primary key: the same column is both.
        var banner = Column(graph, nameof(LinkshellBanner), nameof(LinkshellBanner.LinkshellId));
        Assert.True(banner.Pk);
        Assert.True(banner.Fk);

        var userRole = Table(graph, "IdentityUserRole");
        Assert.Equal(new[] { "UserId", "RoleId" }, userRole.Columns.Where(column => column.Pk).Select(column => column.Name));
        Assert.All(userRole.Columns, column => Assert.True(column.Fk));

        Assert.True(Column(graph, nameof(Event), nameof(Event.LinkshellId)).Fk);
        Assert.False(Column(graph, nameof(Event), nameof(Event.LinkshellId)).Nullable);
        Assert.True(Column(graph, nameof(Event), nameof(Event.SourceTodId)).Nullable);
        // A plain int with no foreign key is not a link (the DKP ledger's pool pointer is deliberately unenforced).
        Assert.False(Column(graph, nameof(DkpLedgerEntry), nameof(DkpLedgerEntry.DkpPoolId)).Fk);
    }

    // ---- relationships ----

    [Fact]
    public void EveryForeignKey_IsOneRelationship_BetweenColumnsOnTheDiagram()
    {
        using var db = NewInMemoryContext();
        var graph = new DataAdminCatalog(db.Model, DataAdminPolicy.Default).Schema;

        Assert.Equal(db.Model.GetEntityTypes().Sum(entityType => entityType.GetForeignKeys().Count()), graph.Relationships.Count);
        Assert.True(graph.Relationships.Count > 50, $"Expected the full model, got {graph.Relationships.Count} foreign keys.");

        // Every line against EF itself, not only the handful pinned by name further down: the right
        // two tables, the right columns, optional or required, and the delete behaviour EF configured.
        static string Names(IEnumerable<IProperty> properties) => string.Join(",", properties.Select(property => property.GetColumnName()));
        Assert.Equal(
            db.Model.GetEntityTypes()
                .SelectMany(entityType => entityType.GetForeignKeys())
                .Select(fk => $"{IdOf(fk.DeclaringEntityType)}({Names(fk.Properties)}) -> {IdOf(fk.PrincipalEntityType)}({Names(fk.PrincipalKey.Properties)}) required={fk.IsRequired} {fk.DeleteBehavior}")
                .OrderBy(text => text, StringComparer.Ordinal),
            graph.Relationships
                .Select(relationship => $"{relationship.From}({string.Join(",", relationship.FromColumns)}) -> {relationship.To}({string.Join(",", relationship.ToColumns)}) required={relationship.Required} {relationship.EfDelete}")
                .OrderBy(text => text, StringComparer.Ordinal));

        foreach (var relationship in graph.Relationships)
        {
            var from = Table(graph, relationship.From);
            var to = Table(graph, relationship.To);

            Assert.NotEmpty(relationship.FromColumns);
            Assert.Equal(relationship.FromColumns.Count, relationship.ToColumns.Count);
            Assert.All(relationship.FromColumns, name => Assert.Contains(from.Columns, column => column.Name == name && column.Fk));
            Assert.All(relationship.ToColumns, name => Assert.Contains(to.Columns, column => column.Name == name));
            Assert.Contains(relationship.OnDelete, new[] { "cascade", "setNull", "restrict" });
        }

        // And the other way round: a column drawn as a foreign key has a line leaving it.
        foreach (var table in graph.Tables)
        {
            foreach (var column in table.Columns.Where(column => column.Fk))
            {
                Assert.Contains(graph.Relationships, relationship => relationship.From == table.Id && relationship.FromColumns.Contains(column.Name));
            }
        }
    }

    // The line style is what Postgres does, not what EF calls it: ClientSetNull and Restrict both
    // migrate without ON DELETE SET NULL / CASCADE, so both block the delete.
    [Fact]
    public void KnownForeignKeys_CarryTheirDeleteRuleAndOptionality()
    {
        var graph = NewGraph();

        var eventLinkshell = Relationship(graph, "Event.LinkshellId");
        Assert.Equal("Event", eventLinkshell.From);
        Assert.Equal(new[] { "LinkshellId" }, eventLinkshell.FromColumns);
        Assert.Equal("Linkshell", eventLinkshell.To);
        Assert.Equal(new[] { "Id" }, eventLinkshell.ToColumns);
        Assert.True(eventLinkshell.Required);
        Assert.Equal("cascade", eventLinkshell.OnDelete);
        Assert.Equal("Cascade", eventLinkshell.EfDelete);

        var eventSourceTod = Relationship(graph, "Event.SourceTodId");
        Assert.Equal("Tod", eventSourceTod.To);
        Assert.False(eventSourceTod.Required);
        Assert.Equal("restrict", eventSourceTod.OnDelete);
        Assert.Equal("ClientSetNull", eventSourceTod.EfDelete);

        Assert.Equal("setNull", Relationship(graph, "EventLootDetail.EventId").OnDelete);
        Assert.Equal("SetNull", Relationship(graph, "EventLootDetail.EventId").EfDelete);
        Assert.Equal("restrict", Relationship(graph, "JournalEntryLine.LedgerAccountId").OnDelete);
        Assert.Equal("Restrict", Relationship(graph, "JournalEntryLine.LedgerAccountId").EfDelete);
        // NoAction is EF's other name for "the database refuses": the same dotted line as Restrict.
        Assert.Equal("restrict", Relationship(graph, "Auction.DkpPoolId").OnDelete);
        Assert.Equal("NoAction", Relationship(graph, "Auction.DkpPoolId").EfDelete);

        var cloned = Relationship(graph, "PartySetup.ClonedFromPartySetupId");
        Assert.Equal("PartySetup", cloned.From);
        Assert.Equal(cloned.From, cloned.To);
    }

    // "Unique" draws the "at most one" line end and the UQ badge, so it needs a rule about EVERY
    // row. "One default pool per linkshell" and "one cash account per linkshell" are partial
    // indexes over LinkshellId: a linkshell still has many pools and many accounts.
    [Fact]
    public void Unique_NeedsARuleAboutEveryRow_NotAPartialIndex()
    {
        var graph = NewGraph();

        // By a shared primary key, by a plain unique index, and by a unique index that only skips
        // the rows with no value.
        Assert.True(Relationship(graph, "LinkshellBanner.LinkshellId").Unique);
        Assert.True(Relationship(graph, "LedgerPeriod.LinkshellId").Unique);
        Assert.True(Relationship(graph, "PartySetup.OwnerEventId").Unique);
        Assert.True(Column(graph, "LedgerPeriod", "LinkshellId").Unique);
        Assert.True(Column(graph, "PartySetup", "OwnerEventId").Unique);
        // The banner's column is the whole primary key: PK already says unique, so no UQ badge.
        Assert.False(Column(graph, "LinkshellBanner", "LinkshellId").Unique);
        Assert.False(Column(graph, "Linkshell", "Id").Unique);

        Assert.False(Relationship(graph, "DkpPool.LinkshellId").Unique);
        Assert.False(Relationship(graph, "LedgerAccount.LinkshellId").Unique);
        Assert.False(Column(graph, "DkpPool", "LinkshellId").Unique);
        Assert.False(Column(graph, "LedgerAccount", "LinkshellId").Unique);

        // A unique column that is not a foreign key at all, and one column of a two-column index.
        Assert.True(Column(graph, "AddonApiToken", "TokenHash").Unique);
        Assert.False(Column(graph, "DkpPool", "Name").Unique);
    }

    // The index list is the whole truth, partial indexes included, with the filter as written.
    [Fact]
    public void Uniques_ListEveryUniqueIndex_WithItsFilter()
    {
        using var db = NewInMemoryContext();
        var graph = new DataAdminCatalog(db.Model, DataAdminPolicy.Default).Schema;

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            Assert.Equal(entityType.GetIndexes().Count(index => index.IsUnique), Table(graph, IdOf(entityType)).Uniques.Count);
        }

        var pool = Table(graph, "DkpPool").Uniques;
        Assert.Contains(pool, unique => unique.Columns.SequenceEqual(new[] { "LinkshellId" }) && unique.Filter == "\"IsDefault\" = TRUE");
        Assert.Contains(pool, unique => unique.Columns.SequenceEqual(new[] { "LinkshellId", "Name" }) && unique.Filter is null);
        Assert.Contains(
            Table(graph, "PartySetup").Uniques,
            unique => unique.Columns.SequenceEqual(new[] { "OwnerEventId" }) && unique.Filter == "\"OwnerEventId\" IS NOT NULL");
    }

    // ---- junctions, hubs, parents ----

    // AppUserLinkshell has two foreign keys but no key over the pair, so the database does not
    // make it a many-to-many; the four below do (a primary key or a unique index over both).
    [Fact]
    public void Junctions_AreTheManyToManysTheDatabaseEnforces()
    {
        var graph = NewGraph();
        var via = graph.Junctions.Select(junction => junction.Via).ToList();

        Assert.Contains("AppUserEventWindow", via);
        Assert.Contains("EventPartySlotSignup", via);
        Assert.Contains("AppUserEventHistory", via);
        Assert.Contains("IdentityUserRole", via);
        Assert.DoesNotContain("AppUserLinkshell", via);

        // By composite primary key.
        var userRole = Assert.Single(graph.Junctions, junction => junction.Via == "IdentityUserRole");
        Assert.Equal("AppUser", userRole.Left);
        Assert.Equal("IdentityRole", userRole.Right);
        Assert.Equal("IdentityUserRole.UserId", userRole.LeftRelationship);
        Assert.Equal("IdentityUserRole.RoleId", userRole.RightRelationship);

        // By a unique index filtered on its own column; the index is (EventHistoryId, AppUserId),
        // the sides are in table order.
        var history = Assert.Single(graph.Junctions, junction => junction.Via == "AppUserEventHistory");
        Assert.Equal("AppUser", history.Left);
        Assert.Equal("EventHistory", history.Right);
        Assert.Equal("AppUserEventHistory.AppUserId", history.LeftRelationship);
        Assert.Equal("AppUserEventHistory.EventHistoryId", history.RightRelationship);

        foreach (var junction in graph.Junctions)
        {
            Assert.True(string.CompareOrdinal(junction.Left, junction.Right) < 0, $"{junction.Via}: {junction.Left} / {junction.Right}");
            var left = Relationship(graph, junction.LeftRelationship);
            var right = Relationship(graph, junction.RightRelationship);
            Assert.Equal(junction.Via, left.From);
            Assert.Equal(junction.Left, left.To);
            Assert.Equal(junction.Via, right.From);
            Assert.Equal(junction.Right, right.To);
        }
    }

    [Fact]
    public void Hubs_AreTheTablesMostForeignKeysPointAt()
    {
        using var db = NewInMemoryContext();
        var graph = new DataAdminCatalog(db.Model, DataAdminPolicy.Default).Schema;

        Assert.True(Table(graph, "Linkshell").Hub);
        Assert.True(Table(graph, "AppUser").Hub);
        Assert.False(Table(graph, "Tod").Hub);
        Assert.False(Table(graph, "PartySetup").Hub);

        // Do not pin how many hubs there are, only the rule: inbound foreign keys from OTHER tables,
        // counted from EF itself so the graph is not being compared with its own arithmetic.
        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var inbound = entityType.GetReferencingForeignKeys().Count(fk => fk.DeclaringEntityType != entityType);
            Assert.Equal(inbound >= DataAdminDefaults.SchemaHubMinInbound, Table(graph, IdOf(entityType)).Hub);
        }
    }

    // Parent is the layout hint: the table a card is placed beside.
    [Fact]
    public void Parents_FollowCascadeOwnership_ThenFallBackToWhatATablePointsAt()
    {
        var graph = NewGraph();

        // Linkshell points at AppUser (its owner, optional) but is a hub, so it never falls back:
        // nesting it under AppUser would drag every linkshell table along.
        Assert.Null(Table(graph, "Linkshell").Parent);
        Assert.Null(Table(graph, "AppUser").Parent);
        Assert.Null(Table(graph, "AppSetting").Parent);

        Assert.Equal("Linkshell", Table(graph, "Event").Parent);
        Assert.Equal("Event", Table(graph, "AppUserEvent").Parent);
        // Deepest cascade owner, as on "Choose tables".
        Assert.Equal("DkpPool", Table(graph, "DkpPoolEventType").Parent);
        // No cascade owner (SetNull): falls back to the one table it points at.
        Assert.Equal("AppUser", Table(graph, "DiscordActivityUser").Parent);
        // AppUser and IdentityRole both cascade into it and tie; the name decides.
        Assert.Equal("AppUser", Table(graph, "IdentityUserRole").Parent);
    }

    [Fact]
    public void EveryParentChain_Ends()
    {
        var graph = NewGraph();
        var parentOf = graph.Tables.ToDictionary(table => table.Id, table => table.Parent);

        foreach (var table in graph.Tables)
        {
            var seen = new HashSet<string> { table.Id };
            for (var cursor = table.Parent; cursor is not null; cursor = parentOf[cursor])
            {
                Assert.Contains(cursor, parentOf.Keys);
                Assert.True(seen.Add(cursor), $"{table.Id}: the parent chain loops at {cursor}.");
            }
        }
    }

    // ---- the pure rules ----

    // Postgres spells its common types at length; the card shows the alias. Anything without a
    // shorter name is shown as it is. The CLR type and length are ignored once the database has
    // named the type.
    [Theory]
    [InlineData("character varying(32)", "varchar(32)")]
    [InlineData("character varying", "varchar")]
    [InlineData("timestamp with time zone", "timestamptz")]
    [InlineData("timestamp without time zone", "timestamp")]
    [InlineData("double precision", "float8")]
    [InlineData("integer", "int")]
    [InlineData("boolean", "bool")]
    [InlineData("text", "text")]
    [InlineData("jsonb", "jsonb")]
    [InlineData("bigint", "bigint")]
    [InlineData("numeric(18,2)", "numeric(18,2)")]
    [InlineData("integer[]", "integer[]")]
    public void DisplayType_ShortensThePostgresStoreType(string storeType, string expected)
    {
        Assert.Equal(expected, DataAdminSchemaGraph.DisplayType(storeType, typeof(Guid), 99));
    }

    // The InMemory test provider has no store types, so the type is guessed from the CLR type the
    // way Npgsql maps it in this app (every array property here is a jsonb column).
    [Theory]
    [InlineData(typeof(string), 64, "varchar(64)")]
    [InlineData(typeof(string), null, "text")]
    [InlineData(typeof(int), null, "int")]
    [InlineData(typeof(int?), null, "int")]
    [InlineData(typeof(int), 10, "int")]
    [InlineData(typeof(long), null, "bigint")]
    [InlineData(typeof(short), null, "smallint")]
    [InlineData(typeof(bool), null, "bool")]
    [InlineData(typeof(double?), null, "float8")]
    [InlineData(typeof(float), null, "float4")]
    [InlineData(typeof(decimal), null, "numeric")]
    [InlineData(typeof(DateTime), null, "timestamptz")]
    [InlineData(typeof(DateTimeOffset?), null, "timestamptz")]
    [InlineData(typeof(Guid), null, "uuid")]
    [InlineData(typeof(byte[]), null, "bytea")]
    [InlineData(typeof(int[]), null, "jsonb")]
    [InlineData(typeof(string[]), null, "jsonb")]
    [InlineData(typeof(DayOfWeek), null, "int")]
    [InlineData(typeof(DayOfWeek?), null, "int")]
    [InlineData(typeof(TimeSpan), null, "timespan")]
    public void DisplayType_FallsBackToTheClrType_WhenThereIsNoStoreType(Type clrType, int? maxLength, string expected)
    {
        Assert.Equal(expected, DataAdminSchemaGraph.DisplayType(null, clrType, maxLength));
    }

    // Checked against an index on (OwnerEventId, AppUserId). Only "<own column>" IS NOT NULL
    // leaves a unique index meaning "a value appears once"; anything else is a rule about some rows.
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("\"OwnerEventId\" IS NOT NULL", true)]
    [InlineData("\"AppUserId\" IS NOT NULL", true)]
    [InlineData("  \"OwnerEventId\" is not null ", true)]
    [InlineData("\"IsDefault\" = TRUE", false)]
    [InlineData("\"IsCash\" = TRUE", false)]
    // A column the index does not cover.
    [InlineData("\"LinkshellId\" IS NOT NULL", false)]
    // Quoted names are case-sensitive in Postgres, and an unquoted one folds to lower case: both
    // name a different column.
    [InlineData("\"ownereventid\" IS NOT NULL", false)]
    [InlineData("OwnerEventId IS NOT NULL", false)]
    [InlineData("\"OwnerEventId\" IS NULL", false)]
    [InlineData("\"OwnerEventId\" IS NOT NULL AND \"IsDefault\" = TRUE", false)]
    [InlineData("\"OwnerEventId\" IS NOT NULL OR TRUE", false)]
    public void IsPlainUniqueFilter_AcceptsOnlyNoFilter_OrItsOwnColumnIsNotNull(string? filter, bool expected)
    {
        Assert.Equal(expected, DataAdminSchemaGraph.IsPlainUniqueFilter(filter, new[] { "OwnerEventId", "AppUserId" }));
    }

    // ---- JSON ----

    // The contract the page's scripts read: camelCase names, and a flag that is false (or a value
    // that is null) is simply not there.
    [Fact]
    public void Json_IsCamelCase_AndLeavesOutFalseFlagsAndNulls()
    {
        var graph = NewGraph();
        using var document = JsonDocument.Parse(graph.Json);
        var root = document.RootElement;
        var properties = Properties(root).ToList();

        Assert.Equal(new[] { "tables", "relationships", "junctions" }, root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(graph.Tables.Count, root.GetProperty("tables").GetArrayLength());
        Assert.Equal(graph.Relationships.Count, root.GetProperty("relationships").GetArrayLength());
        Assert.Equal(graph.Junctions.Count, root.GetProperty("junctions").GetArrayLength());

        var expectedNames = new[]
        {
            "tables", "relationships", "junctions",
            "id", "name", "label", "slug", "framework", "hub", "parent", "columns", "uniques",
            "type", "storeType", "pk", "fk", "unique", "nullable", "protected", "filter",
            "from", "fromColumns", "to", "toColumns", "required", "onDelete", "efDelete",
            "via", "left", "right", "leftRelationship", "rightRelationship",
        };
        Assert.Equal(
            expectedNames.OrderBy(name => name, StringComparer.Ordinal),
            properties.Select(property => property.Name).Distinct().OrderBy(name => name, StringComparer.Ordinal));
        Assert.DoesNotContain(properties, property => property.Value.ValueKind is JsonValueKind.False or JsonValueKind.Null);

        var linkshell = root.GetProperty("tables").EnumerateArray().Single(table => table.GetProperty("id").GetString() == "Linkshell");
        Assert.True(linkshell.GetProperty("hub").GetBoolean());
        Assert.Equal("linkshell", linkshell.GetProperty("slug").GetString());
        Assert.False(linkshell.TryGetProperty("framework", out _));
        Assert.False(linkshell.TryGetProperty("parent", out _));
        Assert.Equal(
            new[] { "name", "type", "storeType", "pk" },
            linkshell.GetProperty("columns")[0].EnumerateObject().Select(property => property.Name));

        var userRole = root.GetProperty("tables").EnumerateArray().Single(table => table.GetProperty("id").GetString() == "IdentityUserRole");
        Assert.True(userRole.GetProperty("framework").GetBoolean());
        Assert.False(userRole.TryGetProperty("slug", out _));
        Assert.Equal("AppUser", userRole.GetProperty("parent").GetString());

        var optional = root.GetProperty("relationships").EnumerateArray().Single(relationship => relationship.GetProperty("id").GetString() == "Event.SourceTodId");
        Assert.False(optional.TryGetProperty("required", out _));
        Assert.False(optional.TryGetProperty("unique", out _));
        Assert.Equal("Event", optional.GetProperty("from").GetString());
        Assert.Equal("SourceTodId", optional.GetProperty("fromColumns")[0].GetString());
        Assert.Equal("Tod", optional.GetProperty("to").GetString());
        Assert.Equal("Id", optional.GetProperty("toColumns")[0].GetString());
        Assert.Equal("restrict", optional.GetProperty("onDelete").GetString());
        Assert.Equal("ClientSetNull", optional.GetProperty("efDelete").GetString());
    }

    // The JSON is written into a <script type="application/json"> block. The real model has no
    // name that could close it, so this one is given a table name that tries to.
    [Fact]
    public void Synthetic_ATableName_CannotCloseTheScriptBlock()
    {
        var graph = NewOddShapesGraph();

        Assert.Equal(HostileTableName, Table(graph, "Zone").Name);
        Assert.DoesNotContain("</script", graph.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('<', graph.Json);
        Assert.DoesNotContain("</script", NewGraph().Json, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(graph.Json);
        var zone = document.RootElement.GetProperty("tables").EnumerateArray().Single(table => table.GetProperty("id").GetString() == "Zone");
        Assert.Equal(HostileTableName, zone.GetProperty("name").GetString());
    }

    // ---- the production provider ----

    // Everything above runs on the InMemory provider, where a table is named after its CLR type
    // and no column has a store type. This is the path production takes; building the model needs
    // no database and the connection is never opened.
    [Fact]
    public void NpgsqlModel_GivesTheRealTableNamesAndStoreTypes_WithoutADatabase()
    {
        using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=schema_graph_test")
            .Options);
        var graph = new DataAdminCatalog(db.Model, DataAdminPolicy.Default).Schema;
        var inMemory = NewGraph();

        Assert.Equal("Linkshells", Table(graph, "Linkshell").Name);
        Assert.Equal("AspNetUsers", Table(graph, "AppUser").Name);
        Assert.Equal("AspNetUserRoles", Table(graph, "IdentityUserRole").Name);
        Assert.Equal("Linkshell", Table(inMemory, "Linkshell").Name);

        var storeTypes = graph.Tables.SelectMany(table => table.Columns).Select(column => column.StoreType).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("integer", storeTypes);
        Assert.Contains("jsonb", storeTypes);
        Assert.Contains("timestamp with time zone", storeTypes);

        var id = Column(graph, "Linkshell", "Id");
        Assert.Equal("integer", id.StoreType);
        Assert.Equal("int", id.Type);
        var jobLevels = Column(graph, nameof(AppUserLinkshell), nameof(AppUserLinkshell.JobLevels));
        Assert.Equal("jsonb", jobLevels.StoreType);
        Assert.True(jobLevels.Protected);
        var poolName = Column(graph, nameof(DkpPool), nameof(DkpPool.Name));
        Assert.Equal("character varying(64)", poolName.StoreType);
        Assert.Equal("varchar(64)", poolName.Type);
        var createdAt = Column(graph, nameof(AddonApiToken), nameof(AddonApiToken.CreatedAt));
        Assert.Equal("timestamp with time zone", createdAt.StoreType);
        Assert.Equal("timestamptz", createdAt.Type);

        // The structure is the same graph on both providers, which is what lets every other test
        // here stand in for production.
        Assert.Equal(inMemory.Tables.Select(table => table.Id), graph.Tables.Select(table => table.Id));
        Assert.Equal(inMemory.Relationships.Select(relationship => relationship.Id), graph.Relationships.Select(relationship => relationship.Id));
        Assert.Equal(inMemory.Relationships.Select(Describe), graph.Relationships.Select(Describe));
        Assert.Equal(inMemory.Junctions, graph.Junctions);
        Assert.Equal(inMemory.Tables.Select(Describe), graph.Tables.Select(Describe));
    }

    // Everything about a relationship / a table that must not depend on the provider.
    private static string Describe(DataAdminSchemaRelationship relationship) =>
        $"{relationship.Id}: {relationship.From}({string.Join(",", relationship.FromColumns)}) -> {relationship.To}({string.Join(",", relationship.ToColumns)})"
        + $" required={relationship.Required} unique={relationship.Unique} {relationship.OnDelete}/{relationship.EfDelete}";

    private static string Describe(DataAdminSchemaTable table) =>
        $"{table.Id} label={table.Label} slug={table.Slug} framework={table.Framework} hub={table.Hub} parent={table.Parent}: "
        + string.Join(", ", table.Columns.Select(column => $"{column.Name} pk={column.Pk} fk={column.Fk} unique={column.Unique} nullable={column.Nullable} protected={column.Protected}"))
        + " | "
        + string.Join(", ", table.Uniques.Select(unique => $"({string.Join(",", unique.Columns)}) {unique.Filter}"));

    // ---- shapes the real model does not have ----

    // The catalog is built at boot, so the graph must not be: a bug in the builder may break the
    // schema page but never startup. Two Identity tables that would share one id make the builder
    // throw, which shows exactly when it runs.
    [Fact]
    public void Synthetic_TwoTablesWithOneId_FailWhenTheSchemaIsRead_NotWhenTheCatalogIsBuilt()
    {
        using var db = new TwoUserRoleTablesContext(new DbContextOptionsBuilder<TwoUserRoleTablesContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var catalog = new DataAdminCatalog(db.Model, DataAdminPolicy.Default);
        var error = Assert.Throws<InvalidOperationException>(() => catalog.Schema);

        Assert.Empty(catalog.Models);
        Assert.Contains("IdentityUserRole<string>", error.Message, StringComparison.Ordinal);
        Assert.Contains("IdentityUserRole<int>", error.Message, StringComparison.Ordinal);
        Assert.Contains("'IdentityUserRole'", error.Message, StringComparison.Ordinal);
    }

    // What becomes a card: every table of its own. A shared-type entity is one (named by the last
    // part of its EF name, drawn structure-only like the Identity tables); an owned type lives in
    // its owner's table and is not.
    [Fact]
    public void Synthetic_SharedTypeEntitiesAreDrawn_OwnedTypesAreNot()
    {
        using var db = NewOddShapesContext();
        var graph = new DataAdminCatalog(db.Model, DataAdminPolicy.Default).Schema;

        Assert.Contains(db.Model.GetEntityTypes(), entityType => entityType.IsOwned());
        Assert.Equal(
            new[] { "Camp", "Region", "RegionZone", "Rivalry", "Tag", "Visit", "Zone" },
            graph.Tables.Select(table => table.Id));

        var tag = Table(graph, "Tag");
        Assert.True(tag.Framework);
        Assert.Null(tag.Slug);
        Assert.Equal("region", Table(graph, "Region").Slug);
    }

    // Shadow properties are real columns, but the catalog model has nothing to bind them to and
    // does not carry them: they follow its columns, by name.
    [Fact]
    public void Synthetic_ColumnsTheCatalogModelDoesNotCarry_FollowItsColumnsByName()
    {
        var graph = NewOddShapesGraph();

        Assert.Equal(new[] { "Id", "Name", "Altitude", "Weight" }, Table(graph, "Region").Columns.Select(column => column.Name));
    }

    [Fact]
    public void Synthetic_RelationshipIds_JoinTheColumns_AndNameTheTargetWhenAColumnPointsAtTwoTables()
    {
        var graph = NewOddShapesGraph();

        var composite = Relationship(graph, "Visit.CampRegionId+CampNumber");
        Assert.Equal("Camp", composite.To);
        Assert.Equal(new[] { "CampRegionId", "CampNumber" }, composite.FromColumns);
        Assert.Equal(new[] { "RegionId", "Number" }, composite.ToColumns);
        // A unique index sits exactly on the pair, so the line is one-to-one; neither column is
        // unique alone.
        Assert.True(composite.Unique);
        Assert.False(Column(graph, "Visit", "CampRegionId").Unique);
        Assert.False(Column(graph, "Visit", "CampNumber").Unique);

        var fromTarget = graph.Relationships
            .Where(relationship => relationship.From == "Visit" && relationship.FromColumns.SequenceEqual(new[] { "TargetId" }))
            .ToList();
        Assert.Equal(new[] { "Region", "Zone" }, fromTarget.Select(relationship => relationship.To).OrderBy(to => to, StringComparer.Ordinal));
        Assert.Single(fromTarget, relationship => relationship.Id == "Visit.TargetId");
        var second = Assert.Single(fromTarget, relationship => relationship.Id != "Visit.TargetId");
        Assert.Equal($"Visit.TargetId>{second.To}", second.Id);
    }

    // Three near misses and one hit, each a unique pair of foreign-key columns: Rivalry points at
    // Region twice, Zone's pair includes a pointer to itself, Visit's pair is ONE two-column
    // foreign key. Only RegionZone links two different other tables.
    [Fact]
    public void Synthetic_AJunction_LinksTwoDifferentOtherTables()
    {
        var graph = NewOddShapesGraph();

        var junction = Assert.Single(graph.Junctions);
        Assert.Equal("RegionZone", junction.Via);
        Assert.Equal("Region", junction.Left);
        Assert.Equal("Zone", junction.Right);
        Assert.Equal("RegionZone.RegionId", junction.LeftRelationship);
        Assert.Equal("RegionZone.ZoneId", junction.RightRelationship);
    }

    // The catalog always hands over every table, so this cannot happen through it; the builder
    // still must not draw a line to a card that is not there.
    [Fact]
    public void Synthetic_AForeignKeyToATableThatIsNotDrawn_IsLeftOut()
    {
        using var db = NewOddShapesContext();

        var graph = DataAdminSchemaGraph.Build(
            Array.Empty<DataAdminModel>(),
            new[] { EntityType(db, typeof(Visit)), EntityType(db, typeof(Camp)) });

        var only = Assert.Single(graph.Relationships);
        Assert.Equal("Visit.CampRegionId+CampNumber", only.Id);
        Assert.Equal("Camp", Table(graph, "Visit").Parent);
        Assert.Null(Table(graph, "Camp").Parent);
    }

    public class Climate
    {
        public string Kind { get; set; } = string.Empty;
    }

    public class Region
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Climate Climate { get; set; } = new();
    }

    public class Zone
    {
        public int Id { get; set; }
        public int? ParentZoneId { get; set; }
        public int RegionId { get; set; }
    }

    public class Camp
    {
        public int Id { get; set; }
        public int RegionId { get; set; }
        public int Number { get; set; }
    }

    public class Visit
    {
        public int Id { get; set; }
        public int CampRegionId { get; set; }
        public int CampNumber { get; set; }
        public int TargetId { get; set; }
    }

    public class Rivalry
    {
        public int Id { get; set; }
        public int FirstRegionId { get; set; }
        public int SecondRegionId { get; set; }
    }

    public class RegionZone
    {
        public int Id { get; set; }
        public int RegionId { get; set; }
        public int ZoneId { get; set; }
    }

    private sealed class OddShapesContext : DbContext
    {
        public OddShapesContext(DbContextOptions<OddShapesContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Region>(region =>
            {
                region.Property<int>("Weight");
                region.Property<int>("Altitude");
                region.OwnsOne(row => row.Climate);
            });
            builder.Entity<Zone>(zone =>
            {
                zone.ToTable(HostileTableName);
                zone.HasOne<Zone>().WithMany().HasForeignKey(row => row.ParentZoneId);
                zone.HasOne<Region>().WithMany().HasForeignKey(row => row.RegionId);
                zone.HasIndex(row => new { row.ParentZoneId, row.RegionId }).IsUnique();
            });
            builder.Entity<Camp>(camp => camp.HasOne<Region>().WithMany().HasForeignKey(row => row.RegionId));
            builder.Entity<Visit>(visit =>
            {
                visit.HasOne<Camp>().WithMany()
                    .HasForeignKey(row => new { row.CampRegionId, row.CampNumber })
                    .HasPrincipalKey(camp => new { camp.RegionId, camp.Number });
                visit.HasIndex(row => new { row.CampRegionId, row.CampNumber }).IsUnique();
                visit.HasOne<Region>().WithMany().HasForeignKey(row => row.TargetId);
                visit.HasOne<Zone>().WithMany().HasForeignKey(row => row.TargetId);
            });
            builder.Entity<Rivalry>(rivalry =>
            {
                rivalry.HasOne<Region>().WithMany().HasForeignKey(row => row.FirstRegionId);
                rivalry.HasOne<Region>().WithMany().HasForeignKey(row => row.SecondRegionId);
                rivalry.HasIndex(row => new { row.FirstRegionId, row.SecondRegionId }).IsUnique();
            });
            builder.Entity<RegionZone>(link =>
            {
                link.HasOne<Region>().WithMany().HasForeignKey(row => row.RegionId);
                link.HasOne<Zone>().WithMany().HasForeignKey(row => row.ZoneId);
                link.HasIndex(row => new { row.RegionId, row.ZoneId }).IsUnique();
            });
            builder.SharedTypeEntity<Dictionary<string, object>>("Odd.Shapes.Tag", tag =>
            {
                tag.Property<int>("Id");
                tag.Property<string>("Text");
            });
        }
    }

    private sealed class TwoUserRoleTablesContext : DbContext
    {
        public TwoUserRoleTablesContext(DbContextOptions<TwoUserRoleTablesContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<IdentityUserRole<string>>().HasKey(row => new { row.UserId, row.RoleId });
            builder.Entity<IdentityUserRole<int>>().HasKey(row => new { row.UserId, row.RoleId });
        }
    }
}
