using System.Text.RegularExpressions;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The Data Admin catalog is the whole universe of tables /data-admin can reach, discovered from the
// EF model at startup rather than listed by hand. That makes the SAFETY edges of the discovery the
// thing to pin: the six Identity framework tables never appear, credential / blob / jsonb columns
// never land in any visible column set, every table has one supported key and a label, and slugs
// are unique and never collide with a literal route segment. A regression in any of these would
// silently expose a password hash or make a table unreachable.
public class DataAdminCatalogTests
{
    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static DataAdminCatalog NewCatalog()
    {
        using var db = NewInMemoryContext();
        return new DataAdminCatalog(db.Model, DataAdminPolicy.Default);
    }

    private static DataAdminModel Table(DataAdminCatalog catalog, Type entityType) =>
        catalog.FindByClrType(entityType)
        ?? throw new InvalidOperationException($"{entityType.Name} is not in the catalog.");

    [Fact]
    public void Catalog_SkipsIdentityFrameworkTables_ButKeepsAppUser()
    {
        var catalog = NewCatalog();

        Assert.DoesNotContain(catalog.Models, m => m.ClrType.Namespace == "Microsoft.AspNetCore.Identity");
        Assert.Contains(catalog.Models, m => m.ClrType == typeof(AppUser));
        // Do not pin the exact number: a new entity must not break this test. It only proves the
        // discovery walked the real model rather than an empty one.
        Assert.True(catalog.Models.Count > 50, $"Expected the full model, got {catalog.Models.Count} tables.");
    }

    [Fact]
    public void EveryTable_HasExactlyOneSupportedKey()
    {
        foreach (var table in NewCatalog().Models)
        {
            Assert.True(table.Key.IsKey, $"{table.ClrName}: key column is not marked as the key.");
            Assert.True(DataAdminKeys.IsSupported(table.KeyType), $"{table.ClrName}: key type {table.KeyType.Name} is unsupported.");
            Assert.Single(table.AllColumns, column => column.IsKey);
        }
    }

    // Keys are read from metadata, never assumed to be called Id.
    [Fact]
    public void Keys_ComeFromMetadata_NotFromANameConvention()
    {
        var catalog = NewCatalog();

        Assert.Equal("Key", Table(catalog, typeof(AppSetting)).Key.Name);
        Assert.Equal(typeof(string), Table(catalog, typeof(AppSetting)).KeyType);
        Assert.Equal("LinkshellId", Table(catalog, typeof(LinkshellBanner)).Key.Name);
        Assert.Equal(typeof(Guid), Table(catalog, typeof(DiscordActivityUser)).KeyType);
        Assert.Equal(typeof(string), Table(catalog, typeof(AppUser)).KeyType);
        Assert.Equal(typeof(int), Table(catalog, typeof(Rule)).KeyType);
    }

    // The protected-column sweep: every byte[] / array property in Models/, every IdentityUser
    // internal, and the named credential columns must be absent from every visible set of the
    // table that owns them.
    [Fact]
    public void ProtectedColumns_NeverAppearInAnyVisibleSet()
    {
        var catalog = NewCatalog();
        var credentials = new (Type Entity, string Column)[]
        {
            (typeof(AddonApiToken), nameof(AddonApiToken.TokenHash)),
            (typeof(AddonPairingCode), nameof(AddonPairingCode.Code)),
            (typeof(LinkshellDiscordWebhook), nameof(LinkshellDiscordWebhook.Url)),
            (typeof(Linkshell), nameof(Linkshell.GoogleOAuthRefreshTokenEnc)),
        };

        foreach (var table in catalog.Models)
        {
            var mustHide = table.ClrType.GetProperties()
                .Where(property => property.PropertyType == typeof(byte[]) || property.PropertyType.IsArray)
                .Select(property => property.Name)
                .ToList();
            if (typeof(IdentityUser).IsAssignableFrom(table.ClrType))
            {
                mustHide.AddRange(DataAdminDefaults.IdentityInternals);
            }
            mustHide.AddRange(credentials.Where(c => c.Entity == table.ClrType).Select(c => c.Column));

            foreach (var name in mustHide)
            {
                foreach (var (setName, set) in VisibleSets(table))
                {
                    Assert.False(set.Any(column => column.Name == name), $"{table.ClrName}.{name} is exposed in {setName}.");
                }
            }
        }

        // The sweep above is only meaningful if it actually saw the known hazards.
        Assert.Contains(typeof(AppUser).GetProperties(), p => p.Name == nameof(AppUser.ProfileImage));
        Assert.Contains(typeof(AppUser).GetProperties(), p => p.Name == nameof(AppUser.Alt1JobLevels));
        Assert.Contains(typeof(JobRating).GetProperties(), p => p.PropertyType.IsArray);
        Assert.Contains(typeof(LinkshellBanner).GetProperties(), p => p.PropertyType == typeof(byte[]));
    }

    private static IEnumerable<(string Name, IReadOnlyList<DataAdminColumn> Columns)> VisibleSets(DataAdminModel table)
    {
        yield return (nameof(table.ListColumns), table.ListColumns);
        yield return (nameof(table.DetailColumns), table.DetailColumns);
        yield return (nameof(table.SearchColumns), table.SearchColumns);
        yield return (nameof(table.EditableColumns), table.EditableColumns);
        yield return (nameof(table.CreateColumns), table.CreateColumns);
        yield return (nameof(table.FilterColumns), table.FilterColumns);
        yield return (nameof(table.LabelColumns), table.LabelColumns);
    }

    [Fact]
    public void ListColumns_AreBounded_AndAlwaysIncludeTheKey()
    {
        foreach (var table in NewCatalog().Models)
        {
            Assert.Contains(table.Key, table.ListColumns);
            Assert.True(
                table.ListColumns.Count <= table.LabelColumns.Count + 1 + DataAdminDefaults.MaxListColumns,
                $"{table.ClrName} lists {table.ListColumns.Count} columns.");
            Assert.Equal(table.ListColumns.Count, table.ListColumns.Distinct().Count());
        }
    }

    [Fact]
    public void Slugs_AreUniqueKebabCase_AndNeverRouteSegments()
    {
        var catalog = NewCatalog();
        var slugs = catalog.Models.Select(m => m.Slug).ToList();

        Assert.Equal(slugs.Count, slugs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(slugs, slug => Assert.Matches(new Regex("^[a-z0-9-]+$"), slug));
        Assert.All(slugs, slug => Assert.DoesNotContain(slug, DataAdminDefaults.ReservedSlugs));
        Assert.Same(Table(catalog, typeof(Rule)), catalog.Find("rule"));
        Assert.Same(Table(catalog, typeof(DkpPoolEventType)), catalog.Find("dkp-pool-event-type"));
        Assert.Same(Table(catalog, typeof(AppUser)), catalog.Find("APP-USER"));
        Assert.Null(catalog.Find("tables"));
    }

    // The forest and the delete preview read DeleteBehavior straight from metadata, and EF's
    // conventions decide it for attribute-only foreign keys; these three edges are the ones the
    // rest of the engine reasons about, so a provider-specific surprise would show here first.
    [Fact]
    public void KnownForeignKeys_KeepTheirDeleteBehaviour()
    {
        var catalog = NewCatalog();
        var evt = Table(catalog, typeof(Event));

        Assert.Equal(DeleteBehavior.Cascade, evt.FindColumn(nameof(Event.LinkshellId))?.ForeignKey?.DeleteBehavior);
        Assert.Equal(DeleteBehavior.ClientSetNull, evt.FindColumn(nameof(Event.SourceTodId))?.ForeignKey?.DeleteBehavior);
        Assert.Equal(DeleteBehavior.SetNull, Table(catalog, typeof(EventLootDetail)).FindColumn(nameof(EventLootDetail.EventId))?.ForeignKey?.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, Table(catalog, typeof(JournalEntryLine)).FindColumn(nameof(JournalEntryLine.LedgerAccountId))?.ForeignKey?.DeleteBehavior);
    }

    [Fact]
    public void ForeignKeyColumns_PointAtCatalogTables()
    {
        var catalog = NewCatalog();

        Assert.Same(Table(catalog, typeof(Linkshell)), Table(catalog, typeof(Rule)).FindColumn(nameof(Rule.LinkshellId))?.ForeignKeyTo);
        Assert.Same(Table(catalog, typeof(Tod)), Table(catalog, typeof(Event)).FindColumn(nameof(Event.SourceTodId))?.ForeignKeyTo);
        // A plain int with no foreign key is never a link (the DKP ledger's pool pointer is deliberately unenforced).
        Assert.Null(Table(catalog, typeof(DkpLedgerEntry)).FindColumn(nameof(DkpLedgerEntry.DkpPoolId))?.ForeignKey);
        Assert.Null(Table(catalog, typeof(AppUser)).FindColumn(nameof(AppUser.PrimaryLinkshellId))?.ForeignKey);
    }

    [Fact]
    public void ReverseRelations_ListOnlyCatalogTables()
    {
        var catalog = NewCatalog();
        var appUser = Table(catalog, typeof(AppUser));
        var linkshell = Table(catalog, typeof(Linkshell));

        Assert.DoesNotContain(appUser.ReverseRelations, r => r.Dependent.ClrType.Namespace == "Microsoft.AspNetCore.Identity");
        Assert.Contains(appUser.ReverseRelations, r => r.Dependent.ClrType == typeof(Invite) && r.ForeignKeyColumn.Name == nameof(Invite.AppUserId));
        Assert.Contains(linkshell.ReverseRelations, r => r.Dependent.ClrType == typeof(Rule) && r.DeleteBehavior == DeleteBehavior.Cascade);
    }

    [Fact]
    public void Labels_ResolveFromPolicyOrGuess_ElseFallBackToTheKey()
    {
        var catalog = NewCatalog();

        Assert.Equal(new[] { nameof(Rule.RuleTitle) }, Table(catalog, typeof(Rule)).LabelColumns.Select(c => c.Name));
        Assert.Equal(new[] { nameof(Linkshell.LinkshellName) }, Table(catalog, typeof(Linkshell)).LabelColumns.Select(c => c.Name));
        Assert.Equal(new[] { nameof(Tod.MonsterName) }, Table(catalog, typeof(Tod)).LabelColumns.Select(c => c.Name));
        Assert.Equal(new[] { "Key" }, Table(catalog, typeof(AppSetting)).LabelColumns.Select(c => c.Name));
        Assert.Equal(
            new[] { nameof(AppUser.CharacterName), nameof(AppUser.UserName), nameof(AppUser.Email) },
            Table(catalog, typeof(AppUser)).LabelColumns.Select(c => c.Name));

        var rule = new Rule { Id = 7, LinkshellId = 1, RuleTitle = "No ninja looting", RuleDetails = "..." };
        Assert.Equal("No ninja looting", Table(catalog, typeof(Rule)).LabelOf(rule));
        Assert.Equal("7", Table(catalog, typeof(Rule)).KeyToString(Table(catalog, typeof(Rule)).KeyOf(rule)));

        // A blank label falls back to the key rather than to nothing.
        var blank = new Rule { Id = 9, LinkshellId = 1, RuleTitle = " ", RuleDetails = "..." };
        Assert.Equal("9", Table(catalog, typeof(Rule)).LabelOf(blank));

        // So does a table with no label column at all (its label projection is a null constant).
        var unlabelled = catalog.Models.FirstOrDefault(m => m.LabelColumns.Count == 0 && m.KeyType == typeof(int));
        if (unlabelled is not null)
        {
            var row = unlabelled.CreateInstance();
            unlabelled.Key.Property.SetValue(row, 42);
            Assert.Equal("42", unlabelled.LabelOf(row));
        }
    }

    [Fact]
    public void KeyParsing_FollowsTheKeyType()
    {
        var catalog = NewCatalog();

        Assert.True(Table(catalog, typeof(Rule)).TryParseKey("12", out var intKey));
        Assert.Equal(12, intKey);
        Assert.False(Table(catalog, typeof(Rule)).TryParseKey("abc", out _));
        Assert.True(Table(catalog, typeof(AppSetting)).TryParseKey("launcher.download.url", out var stringKey));
        Assert.Equal("launcher.download.url", stringKey);
        Assert.False(Table(catalog, typeof(AppSetting)).TryParseKey(" ", out _));
        var guid = Guid.NewGuid();
        Assert.True(Table(catalog, typeof(DiscordActivityUser)).TryParseKey(guid.ToString(), out var guidKey));
        Assert.Equal(guid, guidKey);
    }

    [Fact]
    public void Create_RefusesCompositeKeys_WithANamedError()
    {
        using var db = new CompositeKeyContext(new DbContextOptionsBuilder<CompositeKeyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var entityType = db.Model.FindEntityType(typeof(TwoPartKey))
            ?? throw new InvalidOperationException("test entity missing");

        var error = Assert.Throws<InvalidOperationException>(() => DataAdminModel.Create(entityType, DataAdminEntityPolicy.FullCrud));

        Assert.Contains("composite", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(TwoPartKey), error.Message, StringComparison.Ordinal);
    }

    // A policy typo must fail catalog construction (and therefore boot), naming the column.
    [Fact]
    public void PolicyNamingAnUnknownColumn_FailsLoudly()
    {
        var policy = new DataAdminPolicy(new Dictionary<Type, DataAdminEntityPolicy>
        {
            [typeof(Rule)] = new() { EditableAllowList = new[] { "RuleTitel" } },
        });
        using var db = NewInMemoryContext();

        var error = Assert.Throws<InvalidOperationException>(() => new DataAdminCatalog(db.Model, policy));

        Assert.Contains("RuleTitel", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(Rule), error.Message, StringComparison.Ordinal);
    }

    public class TwoPartKey
    {
        public int A { get; set; }
        public int B { get; set; }
    }

    private sealed class CompositeKeyContext : DbContext
    {
        public CompositeKeyContext(DbContextOptions<CompositeKeyContext> options)
            : base(options)
        {
        }

        public DbSet<TwoPartKey> TwoPartKeys => Set<TwoPartKey>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<TwoPartKey>().HasKey(row => new { row.A, row.B });
        }
    }
}
