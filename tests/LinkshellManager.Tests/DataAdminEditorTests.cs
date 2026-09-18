using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LinkshellManager.Tests;

// The Data Admin editor is the one write path for every table it can reach, so the invariants
// here are the ones that keep a generic form from becoming a generic hole: only the table's
// editable set is ever written (a posted IsSuperAdmin or key changes nothing), blank required
// values and out-of-vocabulary options are refused before SaveChanges, every date lands as UTC
// (all columns are timestamptz), denormalised copies follow their foreign key, capabilities are
// enforced server-side, and a Postgres refusal becomes a sentence on the form.
public class DataAdminEditorTests
{
    private static readonly AppUser Actor = new() { Id = "admin-1", UserName = "millhouse", IsSuperAdmin = true };
    private static readonly Guid DiscordUserKey = Guid.NewGuid();

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "Kraken LS", LootStructure = "Dkp" });
        db.Linkshells.Add(new Linkshell { Id = 2, LinkshellName = "Titans", LootStructure = "Dkp" });
        db.Rules.Add(new Rule { Id = 1, LinkshellId = 1, LinkshellName = "Kraken LS", RuleTitle = "Loot rules", RuleDetails = "No ninja looting" });
        db.Users.Add(new AppUser { Id = "u1", UserName = "someone", CharacterName = "Millhouse", IsSuperAdmin = false });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, AppUserId = "u1", LinkshellId = 1, CharacterName = "Millhouse", Rank = LinkshellRanks.Member });
        db.DkpPools.Add(new DkpPool { Id = 1, LinkshellId = 1, Name = "Main", IsDefault = true });
        db.DiscordActivityUsers.Add(new DiscordActivityUser { Id = DiscordUserKey, DiscordUserId = "123", Username = "mill" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    private static (DataAdminCatalog Catalog, DataAdminEditor Editor, GlobalSettingsService Settings) Harness(ApplicationDbContext db)
    {
        var catalog = new DataAdminCatalog(db.Model, DataAdminPolicy.Default);
        var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));
        return (catalog, new DataAdminEditor(db, settings, NullLogger<DataAdminEditor>.Instance), settings);
    }

    private static DataAdminModel Table(DataAdminCatalog catalog, Type entityType) =>
        catalog.FindByClrType(entityType) ?? throw new InvalidOperationException($"{entityType.Name} is not in the catalog.");

    private static Dictionary<string, string?> Values(params (string Name, string? Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);

    [Fact]
    public async Task Create_BindsOnlyCreateColumns_AndKeepsTheLinkshellNameCopyInStep()
    {
        using var db = await SeededAsync();
        var (catalog, editor, _) = Harness(db);

        var result = await editor.CreateAsync(
            Table(catalog, typeof(Rule)),
            Values(("LinkshellId", "2"), ("RuleTitle", " Be kind "), ("RuleDetails", "Always"), ("Category", ""), ("Id", "77"), ("LinkshellName", "typed by hand")),
            Actor,
            CancellationToken.None);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        var saved = Assert.Single(await db.Rules.AsNoTracking().Where(r => r.RuleTitle == "Be kind").ToListAsync());
        Assert.Equal(2, saved.LinkshellId);
        Assert.Equal("Titans", saved.LinkshellName);   // synced from the foreign key, not from the form
        Assert.Null(saved.Category);                    // blank nullable -> null
        Assert.NotEqual(77, saved.Id);                  // the generated key is never taken from the form
    }

    [Fact]
    public async Task Create_RefusesABlankRequiredValue_AndSavesNothing()
    {
        using var db = await SeededAsync();
        var (catalog, editor, _) = Harness(db);

        var result = await editor.CreateAsync(Table(catalog, typeof(Rule)), Values(("LinkshellId", "1"), ("RuleTitle", "   "), ("RuleDetails", "x")), Actor, CancellationToken.None);

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors, e => e.Field == "RuleTitle");
        Assert.Contains("required", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.Rules.CountAsync());
    }

    [Fact]
    public async Task Update_WritesOnlyEditableColumns_AndNeverTheKey()
    {
        using var db = await SeededAsync();
        var (catalog, editor, _) = Harness(db);

        var result = await editor.UpdateAsync(Table(catalog, typeof(Rule)), 1, Values(("RuleTitle", "Renamed"), ("Id", "999"), ("LinkshellId", "2")), Actor, CancellationToken.None);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        var rule = await db.Rules.AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.Equal("Renamed", rule.RuleTitle);
        Assert.Equal("No ninja looting", rule.RuleDetails); // absent on edit = unchanged
        Assert.Equal("Titans", rule.LinkshellName);        // re-synced after the foreign key moved
        Assert.False(await db.Rules.AnyAsync(r => r.Id == 999));
    }

    [Fact]
    public async Task Update_HonoursTheAllowList_SoIsSuperAdminCannotBeMinted()
    {
        using var db = await SeededAsync();
        var (catalog, editor, _) = Harness(db);

        var result = await editor.UpdateAsync(
            Table(catalog, typeof(AppUser)),
            "u1",
            Values(("IsSuperAdmin", "true"), ("CharacterName", "Renamed"), ("Email", "x@y.test"), ("PasswordHash", "pwned")),
            Actor,
            CancellationToken.None);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "u1");
        Assert.False(user.IsSuperAdmin);
        Assert.Equal("Renamed", user.CharacterName);
        Assert.Null(user.Email);
        Assert.Null(user.PasswordHash);
    }

    [Fact]
    public async Task Update_ValidatesOptionListsAndLengths_BeforeSaving()
    {
        using var db = await SeededAsync();
        var (catalog, editor, _) = Harness(db);
        var memberships = Table(catalog, typeof(AppUserLinkshell));

        var bad = await editor.UpdateAsync(memberships, 1, Values(("Rank", "Boss")), Actor, CancellationToken.None);
        Assert.Contains("must be one of", Assert.Single(bad.Errors, e => e.Field == "Rank").Message, StringComparison.Ordinal);
        Assert.Equal(LinkshellRanks.Member, (await db.AppUserLinkshells.AsNoTracking().SingleAsync()).Rank);

        var good = await editor.UpdateAsync(memberships, 1, Values(("Rank", LinkshellRanks.Officer)), Actor, CancellationToken.None);
        Assert.True(good.Succeeded);
        Assert.Equal(LinkshellRanks.Officer, (await db.AppUserLinkshells.AsNoTracking().SingleAsync()).Rank);

        var linkshells = Table(catalog, typeof(Linkshell));
        var max = linkshells.FindColumn(nameof(Linkshell.LootStructure))?.MaxLength ?? throw new InvalidOperationException("LootStructure has no max length");
        var tooLong = await editor.UpdateAsync(linkshells, 1, Values(("LootStructure", new string('x', max + 1))), Actor, CancellationToken.None);
        Assert.Contains("characters or fewer", Assert.Single(tooLong.Errors, e => e.Field == "LootStructure").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dates_AlwaysLandAsUtc()
    {
        using var db = await SeededAsync();
        var (catalog, editor, _) = Harness(db);

        var rule = await editor.UpdateAsync(Table(catalog, typeof(Rule)), 1, Values(("CreatedAt", "2026-09-18T10:30")), Actor, CancellationToken.None);
        Assert.True(rule.Succeeded, string.Join("; ", rule.Errors.Select(e => e.Message)));
        var created = (await db.Rules.AsNoTracking().SingleAsync(r => r.Id == 1)).CreatedAt;
        Assert.Equal(DateTimeKind.Utc, created.Kind);
        Assert.Equal(new DateTime(2026, 9, 18, 10, 30, 0, DateTimeKind.Utc), created);

        var discord = await editor.UpdateAsync(Table(catalog, typeof(DiscordActivityUser)), DiscordUserKey, Values(("LastSeenAtUtc", "2026-09-18T10:30")), Actor, CancellationToken.None);
        Assert.True(discord.Succeeded, string.Join("; ", discord.Errors.Select(e => e.Message)));
        var seen = (await db.DiscordActivityUsers.AsNoTracking().SingleAsync()).LastSeenAtUtc;
        Assert.Equal(TimeSpan.Zero, seen.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 10, 30, 0, TimeSpan.Zero), seen);
    }

    // The form re-posts every field. A timestamp comes back at second precision; if it is what
    // the form was shown, it is not a change and the stored sub-second value must survive.
    [Fact]
    public async Task ReposingAnUnchangedTimestamp_KeepsItsFullPrecision()
    {
        using var db = await SeededAsync();
        var (catalog, editor, _) = Harness(db);
        var precise = new DateTime(2026, 1, 2, 10, 30, 45, 123, DateTimeKind.Utc);
        var rule = await db.Rules.SingleAsync(r => r.Id == 1);
        rule.CreatedAt = precise;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var form = await editor.BuildFormAsync(Table(catalog, typeof(Rule)), await db.Rules.AsNoTracking().SingleAsync(r => r.Id == 1), posted: null, Array.Empty<DataAdminFieldError>(), CancellationToken.None);
        var posted = form.Fields.ToDictionary(f => f.Name, f => (string?)f.Value, StringComparer.Ordinal);
        Assert.Equal("2026-01-02T10:30:45", posted["CreatedAt"]);
        posted["RuleTitle"] = "Renamed";

        var result = await editor.UpdateAsync(Table(catalog, typeof(Rule)), 1, posted, Actor, CancellationToken.None);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        var saved = await db.Rules.AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.Equal("Renamed", saved.RuleTitle);
        Assert.Equal(precise, saved.CreatedAt);
    }

    // Tod.Claim / Killed are three-state: null means "not recorded". Saving an unrelated field
    // must not turn that null into false.
    [Fact]
    public async Task NullableBool_StaysNull_WhenNotChosen_AndIsADropdown()
    {
        using var db = await SeededAsync();
        db.Tods.Add(new Tod { Id = 1, LinkshellId = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var (catalog, editor, _) = Harness(db);
        var tods = Table(catalog, typeof(Tod));

        var form = await editor.BuildFormAsync(tods, await db.Tods.AsNoTracking().SingleAsync(), posted: null, Array.Empty<DataAdminFieldError>(), CancellationToken.None);
        var claim = Assert.Single(form.Fields, f => f.Name == nameof(Tod.Claim));
        Assert.Equal("select", claim.InputType);
        Assert.False(claim.IsRequired);
        Assert.Equal(new[] { "true", "false" }, claim.Options?.Select(o => o.Value));
        Assert.Equal(string.Empty, claim.Value);

        var posted = form.Fields.ToDictionary(f => f.Name, f => (string?)f.Value, StringComparer.Ordinal);
        var result = await editor.UpdateAsync(tods, 1, posted, Actor, CancellationToken.None);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Null((await db.Tods.AsNoTracking().SingleAsync()).Claim);

        posted[nameof(Tod.Claim)] = "true";
        Assert.True((await editor.UpdateAsync(tods, 1, posted, Actor, CancellationToken.None)).Succeeded);
        Assert.True((await db.Tods.AsNoTracking().SingleAsync()).Claim);
    }

    [Fact]
    public async Task Capabilities_AreEnforcedServerSide()
    {
        using var db = await SeededAsync();
        var (catalog, editor, _) = Harness(db);

        Assert.True((await editor.CreateAsync(Table(catalog, typeof(AppUser)), Values(("CharacterName", "x")), Actor, CancellationToken.None)).Forbidden);
        Assert.True((await editor.UpdateAsync(Table(catalog, typeof(DkpLedgerEntry)), 1, Values(("Amount", "5")), Actor, CancellationToken.None)).Forbidden);
        Assert.True((await editor.UpdateAsync(Table(catalog, typeof(Rule)), 999, Values(("RuleTitle", "x")), Actor, CancellationToken.None)).NotFound);
    }

    [Fact]
    public async Task CreatingAnAppSetting_TakesItsKey_AndIsVisibleToGlobalSettingsAtOnce()
    {
        using var db = await SeededAsync();
        var (catalog, editor, settings) = Harness(db);
        Assert.Null(await settings.GetLauncherDownloadUrlAsync()); // primes the 30s cache

        var result = await editor.CreateAsync(
            Table(catalog, typeof(AppSetting)),
            Values(("Key", GlobalSettingsService.LauncherDownloadUrlKey), ("Value", "https://example.test/launcher")),
            Actor,
            CancellationToken.None);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal("https://example.test/launcher", await settings.GetLauncherDownloadUrlAsync());

        var blankKey = await editor.CreateAsync(Table(catalog, typeof(AppSetting)), Values(("Key", ""), ("Value", "x")), Actor, CancellationToken.None);
        Assert.Contains(blankKey.Errors, e => e.Field == "Key");
    }

    [Fact]
    public async Task BeforeSave_RecomputesTheDerivedColumn_WhichTheFormCannotSet()
    {
        using var db = await SeededAsync();
        var (catalog, editor, _) = Harness(db);

        var result = await editor.CreateAsync(
            Table(catalog, typeof(DkpPoolEventType)),
            Values(("LinkshellId", "1"), ("DkpPoolId", "1"), ("EventType", "  Dynamis "), ("NormalizedEventType", "WRONG")),
            Actor,
            CancellationToken.None);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        var row = await db.DkpPoolEventTypes.AsNoTracking().SingleAsync();
        Assert.Equal("Dynamis", row.EventType);
        Assert.Equal("DYNAMIS", row.NormalizedEventType);
    }

    [Fact]
    public void DescribeSaveFailure_TurnsPostgresCodesIntoSentences()
    {
        static DbUpdateException Wrap(string sqlState, string? constraint = null, string? column = null) =>
            new("save failed", new PostgresException("boom", "ERROR", "ERROR", sqlState, constraintName: constraint, columnName: column));

        Assert.Contains("IX_LinkshellRoles_LinkshellId_Name", DataAdminEditor.DescribeSaveFailure(Wrap(PostgresErrorCodes.UniqueViolation, "IX_LinkshellRoles_LinkshellId_Name")), StringComparison.Ordinal);
        Assert.Contains("already exists", DataAdminEditor.DescribeSaveFailure(Wrap(PostgresErrorCodes.UniqueViolation)), StringComparison.Ordinal);
        Assert.Contains("rejected", DataAdminEditor.DescribeSaveFailure(Wrap(PostgresErrorCodes.CheckViolation, "CK_ChartPopItems_Kind")), StringComparison.Ordinal);
        Assert.Contains("reference", DataAdminEditor.DescribeSaveFailure(Wrap(PostgresErrorCodes.ForeignKeyViolation)), StringComparison.Ordinal);
        Assert.Equal("RuleTitle cannot be empty.", DataAdminEditor.DescribeSaveFailure(Wrap(PostgresErrorCodes.NotNullViolation, column: "RuleTitle")));
        Assert.Equal("boom", DataAdminEditor.DescribeSaveFailure(Wrap("XX000")));
        Assert.Equal("plain failure", DataAdminEditor.DescribeSaveFailure(new DbUpdateException("wrapper", new InvalidOperationException("plain failure"))));
    }

    [Fact]
    public async Task BuildForm_PicksTheWidgetFromMetadata()
    {
        using var db = await SeededAsync();
        var (catalog, editor, _) = Harness(db);

        var create = await editor.BuildFormAsync(Table(catalog, typeof(Rule)), entity: null, posted: null, Array.Empty<DataAdminFieldError>(), CancellationToken.None);
        Assert.True(create.IsCreate);
        var linkshell = Assert.Single(create.Fields, f => f.Name == nameof(Rule.LinkshellId));
        Assert.Equal("select", linkshell.InputType);
        Assert.Equal(new[] { "1", "2" }, linkshell.Options?.Select(o => o.Value));
        Assert.Equal("Kraken LS (#1)", linkshell.Options?[0].Text);
        Assert.Equal("textarea", Assert.Single(create.Fields, f => f.Name == nameof(Rule.RuleDetails)).InputType);
        Assert.Equal("datetime-local", Assert.Single(create.Fields, f => f.Name == nameof(Rule.CreatedAt)).InputType);
        Assert.DoesNotContain(create.Fields, f => f.Name == nameof(Rule.Id));

        var membership = await db.AppUserLinkshells.AsNoTracking().SingleAsync();
        var edit = await editor.BuildFormAsync(Table(catalog, typeof(AppUserLinkshell)), membership, posted: null, Array.Empty<DataAdminFieldError>(), CancellationToken.None);
        var rank = Assert.Single(edit.Fields, f => f.Name == nameof(AppUserLinkshell.Rank));
        Assert.Equal("select", rank.InputType);
        Assert.Equal(DataAdminOptions.FromConsts(typeof(LinkshellRanks)), rank.Options?.Select(o => o.Value));
        Assert.Equal(LinkshellRanks.Member, rank.Value);
        Assert.False(rank.IsRequired);

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "u1");
        var userForm = await editor.BuildFormAsync(Table(catalog, typeof(AppUser)), user, posted: null, Array.Empty<DataAdminFieldError>(), CancellationToken.None);
        Assert.Equal("checkbox", Assert.Single(userForm.Fields, f => f.Name == nameof(AppUser.IsPlaceholder)).InputType);
        Assert.DoesNotContain(userForm.Fields, f => f.Name == nameof(AppUser.IsSuperAdmin));
    }

    [Fact]
    public async Task BuildForm_UsesAKeyInput_WhenThePrincipalTableIsLarge()
    {
        using var db = NewInMemoryContext();
        for (var id = 1; id <= DataAdminDefaults.MaxSelectRows + 1; id++)
        {
            db.Linkshells.Add(new Linkshell { Id = id, LinkshellName = $"LS {id:000}", LootStructure = "Dkp" });
        }
        await db.SaveChangesAsync();
        var (catalog, editor, _) = Harness(db);

        var form = await editor.BuildFormAsync(Table(catalog, typeof(Rule)), entity: null, posted: null, Array.Empty<DataAdminFieldError>(), CancellationToken.None);

        var linkshell = Assert.Single(form.Fields, f => f.Name == nameof(Rule.LinkshellId));
        Assert.Equal("number", linkshell.InputType);
        Assert.Contains("too many", linkshell.Hint, StringComparison.Ordinal);
        Assert.Null(linkshell.Options);
    }

    [Fact]
    public async Task BuildForm_PrefillsFromTheRow_ThenFromThePostedValuesWithErrors()
    {
        using var db = await SeededAsync();
        var (catalog, editor, _) = Harness(db);
        var rule = await db.Rules.AsNoTracking().SingleAsync(r => r.Id == 1);

        var fromRow = await editor.BuildFormAsync(Table(catalog, typeof(Rule)), rule, posted: null, Array.Empty<DataAdminFieldError>(), CancellationToken.None);
        Assert.False(fromRow.IsCreate);
        Assert.Equal("1", fromRow.Key);
        Assert.Equal("Loot rules", Assert.Single(fromRow.Fields, f => f.Name == nameof(Rule.RuleTitle)).Value);
        Assert.Equal("1", Assert.Single(fromRow.Fields, f => f.Name == nameof(Rule.LinkshellId)).Value);

        var errors = new[] { new DataAdminFieldError("RuleTitle", "RuleTitle is required."), new DataAdminFieldError("", "The database said no.") };
        var rerender = await editor.BuildFormAsync(Table(catalog, typeof(Rule)), rule, Values(("RuleTitle", "   ")), errors, CancellationToken.None);
        var title = Assert.Single(rerender.Fields, f => f.Name == nameof(Rule.RuleTitle));
        Assert.Equal("   ", title.Value);
        Assert.Equal("RuleTitle is required.", title.Error);
        Assert.Equal(new[] { "The database said no." }, rerender.FormErrors);
    }
}
