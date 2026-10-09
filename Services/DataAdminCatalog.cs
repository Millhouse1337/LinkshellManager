using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LinkshellManagerDiscordApp.Services;

// Every table the Data Admin UI can ever reach, decided ONCE at startup from the EF model plus
// DataAdminPolicy. This is the only place in the app that enumerates the model
// (DataAdminGateTests pins that), and it is the whole universe: a table that is not here does not
// exist as far as /data-admin is concerned. Every table here that the policy does not hide is
// browsable; there is no runtime selection on top of the policy.
public sealed class DataAdminCatalog
{
    private readonly Dictionary<string, DataAdminModel> _bySlug;
    private readonly Dictionary<string, DataAdminModel> _byClrName;
    private readonly Dictionary<Type, DataAdminModel> _byType;
    private readonly Lazy<DataAdminSchemaGraph> _schema;

    public DataAdminCatalog(IModel model, DataAdminPolicy policy)
    {
        var models = new List<DataAdminModel>();
        // Real tables that are never catalog tables. Only the schema diagram uses them (see Schema).
        var structureOnly = new List<IEntityType>();
        foreach (var entityType in model.GetEntityTypes())
        {
            if (entityType.IsOwned() || entityType.HasSharedClrType || IsIdentityFrameworkType(entityType.ClrType))
            {
                if (!entityType.IsOwned())
                {
                    structureOnly.Add(entityType);
                }
                continue;
            }
            models.Add(DataAdminModel.Create(entityType, policy.For(entityType.ClrType)));
        }

        Models = models.OrderBy(m => m.DisplayName, StringComparer.Ordinal).ToList();
        _bySlug = Models.ToDictionary(m => m.Slug, StringComparer.OrdinalIgnoreCase);
        _byClrName = Models.ToDictionary(m => m.ClrName, StringComparer.Ordinal);
        _byType = Models.ToDictionary(m => m.ClrType);
        foreach (var slug in _bySlug.Keys)
        {
            if (DataAdminDefaults.ReservedSlugs.Contains(slug))
            {
                throw new InvalidOperationException($"Data Admin: table slug '{slug}' collides with a route segment.");
            }
        }

        foreach (var m in Models)
        {
            m.LinkRelations(this);
        }

        Forest = DataAdminCascadeForest<DataAdminModel>.Build(
            Models,
            Models.SelectMany(m => m.CascadePrincipals.Select(principal => (Dependent: m, Principal: principal))),
            m => m.DisplayName,
            m => m.ClrType == typeof(Linkshell));

        _schema = new Lazy<DataAdminSchemaGraph>(() => DataAdminSchemaGraph.Build(Models, structureOnly));
    }

    public IReadOnlyList<DataAdminModel> Models { get; }

    // Cascade-delete grouping (which table's deletion takes which rows with it). Nothing in the app
    // reads it any more; the tests pin its shape over the real model, so it stays.
    public DataAdminCascadeForest<DataAdminModel> Forest { get; }

    // The whole database as a picture, for the schema diagram. Lazy on purpose, and NOT read in the
    // constructor: the catalog is built at boot (Program.cs), and a bug in the graph builder must
    // only ever break the schema page, never startup. The graph also draws the tables the loop
    // above skips (the Identity framework tables). Those are structure only: they are drawn, but
    // they get no model and no slug, so they are never a route to rows.
    public DataAdminSchemaGraph Schema => _schema.Value;

    public DataAdminModel? Find(string? slug) =>
        slug is not null && _bySlug.TryGetValue(slug, out var model) ? model : null;

    public DataAdminModel? FindByClrName(string? clrName) =>
        clrName is not null && _byClrName.TryGetValue(clrName, out var model) ? model : null;

    public DataAdminModel? FindByClrType(Type entityType) =>
        _byType.TryGetValue(entityType, out var model) ? model : null;

    // The six Identity framework tables (roles, claims, logins, tokens) are never catalog tables;
    // AppUser lives in LinkshellManagerDiscordApp.Models and is kept.
    private static bool IsIdentityFrameworkType(Type clrType) =>
        string.Equals(clrType.Namespace, "Microsoft.AspNetCore.Identity", StringComparison.Ordinal);
}
