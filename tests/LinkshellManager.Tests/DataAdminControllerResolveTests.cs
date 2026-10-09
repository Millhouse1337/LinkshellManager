using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Which tables the Data Admin records pages reach, through the real controller over EF InMemory.
// There is no runtime "shown tables" setting any more: every catalog table is browsable unless the
// policy marks it Hidden, and a Hidden table or an unknown slug is a 404 on every page under
// /data-admin/{slug}. The default policy hides nothing, so the Hidden cases use a policy of their
// own. (The authorization gate is a separate, structural test: DataAdminControllerShapeTests.)
public class DataAdminControllerResolveTests
{
    private static readonly DataAdminPolicy RuleHidden = new(new Dictionary<Type, DataAdminEntityPolicy>
    {
        [typeof(Rule)] = new() { Flags = DataAdminPolicyFlags.Hidden },
    });

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "Kraken LS", LootStructure = "Dkp" });
        db.Rules.Add(new Rule { Id = 1, LinkshellId = 1, LinkshellName = "Kraken LS", RuleTitle = "Loot rules", RuleDetails = "..." });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    // The real controller with its real collaborators; only the database is in memory. List reads
    // Request.Query, so every instance gets an (empty) HttpContext.
    private static DataAdminController Controller(ApplicationDbContext db, DataAdminPolicy? policy = null)
    {
        var catalog = new DataAdminCatalog(db.Model, policy ?? DataAdminPolicy.Default);
        var editor = new DataAdminEditor(db, new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions())), NullLogger<DataAdminEditor>.Instance);
        return new DataAdminController(db, catalog, new DataAdminQueryService(db), editor)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    // Every GET and every POST under /data-admin/{slug}: the controller is called directly, so no
    // antiforgery or model binding is involved, and an empty form is enough to reach the resolve.
    [Fact]
    public async Task HiddenTable_IsNotFound_OnEveryPage()
    {
        using var db = await SeededAsync();
        var controller = Controller(db, RuleHidden);
        var none = new Dictionary<string, string?>();

        Assert.IsType<NotFoundResult>(await controller.List("rule", CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Details("rule", "1", CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Create("rule", CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Edit("rule", "1", CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Delete("rule", "1", CancellationToken.None));

        Assert.IsType<NotFoundResult>(await controller.Create("rule", none, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Edit("rule", "1", none, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.DeleteConfirmed("rule", "1", CancellationToken.None));
    }

    [Fact]
    public async Task UnknownSlug_IsNotFound()
    {
        using var db = await SeededAsync();

        Assert.IsType<NotFoundResult>(await Controller(db).List("no-such-table", CancellationToken.None));
    }

    // Nothing has to be "chosen" first: a seeded table lists, and so does one with no rows at all.
    [Fact]
    public async Task AnyCatalogTable_Lists_WithoutBeingChosen()
    {
        using var db = await SeededAsync();
        var controller = Controller(db);

        var linkshells = Assert.IsType<ViewResult>(await controller.List("linkshell", CancellationToken.None));
        var model = Assert.IsType<DataAdminListViewModel>(linkshells.Model);
        Assert.Equal("linkshell", model.Slug);
        Assert.Equal(1, model.Total);

        var roles = Assert.IsType<ViewResult>(await controller.List("linkshell-role", CancellationToken.None));
        Assert.Equal("linkshell-role", Assert.IsType<DataAdminListViewModel>(roles.Model).Slug);

        var rule = Assert.IsType<ViewResult>(await controller.Details("rule", "1", CancellationToken.None));
        Assert.Equal("Loot rules", Assert.IsType<DataAdminDetailsViewModel>(rule.Model).Label);
    }

    [Fact]
    public async Task Index_RendersTheSchemaDiagram()
    {
        using var db = await SeededAsync();

        var view = Assert.IsType<ViewResult>(Controller(db).Index());

        var model = Assert.IsType<DataAdminSchemaViewModel>(view.Model);
        Assert.True(model.TableCount > 60, $"Expected the whole model on the diagram, got {model.TableCount} tables.");
        Assert.Equal("in-memory", model.DatabaseName);
        Assert.False(string.IsNullOrWhiteSpace(model.DiagramJson));
    }
}
