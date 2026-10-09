using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// The Data Admin section: a super-admin-only browser over the database. It lands on the schema
// diagram, and the records pages (list, details, create, edit, delete) sit under it, one table per
// slug. Every table the policy does not hide is browsable: a records page resolves its table
// through the catalog, and an unknown slug or a policy-hidden table is a 404 rather than a
// reachable page. DataAdminPolicy is the only protection. The controller is deliberately thin --
// the catalog, the query service and the editor own the logic and are what the tests exercise.
[Authorize]
[SuperAdminOnly]
[Route("data-admin")]
public sealed partial class DataAdminController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly DataAdminCatalog _catalog;
    private readonly DataAdminQueryService _queries;
    private readonly DataAdminEditor _editor;

    public DataAdminController(
        ApplicationDbContext db,
        DataAdminCatalog catalog,
        DataAdminQueryService queries,
        DataAdminEditor editor)
    {
        _db = db;
        _catalog = catalog;
        _queries = queries;
        _editor = editor;
    }

    // The landing page: the whole database as a picture (tables, columns, foreign keys), never
    // rows. It reads the catalog's schema graph and nothing else: no table is queried for rows here.
    [HttpGet("")]
    public IActionResult Index()
    {
        var graph = _catalog.Schema;

        return View(new DataAdminSchemaViewModel
        {
            DatabaseName = DatabaseName(),
            TableCount = graph.Tables.Count,
            ColumnCount = graph.ColumnCount,
            RelationshipCount = graph.Relationships.Count,
            DiagramJson = graph.Json,
        });
    }

    // A table is reachable when it is in the catalog and the policy does not hide it; anything else
    // (an unknown slug, a Hidden table) is a 404 for every page under /data-admin/{slug}.
    private DataAdminModel? Resolve(string? slug) =>
        _catalog.Find(slug) is { IsHidden: false } table ? table : null;

    // The name in the diagram's header. Reading it off the connection object does not open a
    // connection; the in-memory provider (tests) has no connection at all, hence the guard.
    private string DatabaseName() =>
        _db.Database.IsRelational() ? _db.Database.GetDbConnection().Database : "in-memory";
}
