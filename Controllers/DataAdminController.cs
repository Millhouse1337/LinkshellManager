using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// The Data Admin section: a super-admin-only browser over the tables a super admin has chosen
// to show. Every action resolves its table through the catalog AND the runtime selection, so an
// unselected (or policy-hidden) table is a 404 rather than a reachable page. The controller is
// deliberately thin -- the catalog, the selection service, the query service and the editor own
// the logic and are what the tests exercise.
[Authorize]
[SuperAdminOnly]
[Route("data-admin")]
public sealed partial class DataAdminController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly DataAdminCatalog _catalog;
    private readonly DataAdminSelectionService _selection;
    private readonly DataAdminQueryService _queries;
    private readonly ILogger<DataAdminController> _logger;

    public DataAdminController(
        ApplicationDbContext db,
        DataAdminCatalog catalog,
        DataAdminSelectionService selection,
        DataAdminQueryService queries,
        ILogger<DataAdminController> logger)
    {
        _db = db;
        _catalog = catalog;
        _selection = selection;
        _queries = queries;
        _logger = logger;
    }

    // Shown tables as cards, grouped by the root of their cascade-delete tree.
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var shown = await _selection.GetShownAsync(ct);
        var model = new DataAdminIndexViewModel
        {
            ShownCount = shown.Count,
            CatalogCount = _catalog.Models.Count(table => !table.IsHidden),
        };

        foreach (var group in shown.GroupBy(RootOf).OrderBy(g => g.Key.DisplayName, StringComparer.Ordinal))
        {
            var cards = new DataAdminIndexGroup { RootName = group.Key.DisplayName };
            foreach (var table in group.OrderBy(t => t.DisplayName, StringComparer.Ordinal))
            {
                cards.Cards.Add(new DataAdminIndexCard
                {
                    Slug = table.Slug,
                    DisplayName = table.DisplayName,
                    TableName = table.TableName,
                    RowCount = await table.CountAsync(_db, ct),
                    CanCreate = table.CanCreate,
                    CanEdit = table.CanEdit,
                    CanDelete = table.CanDelete,
                    DiscordSideEffect = table.Policy.DiscordSideEffect,
                });
            }
            model.Groups.Add(cards);
        }

        return View(model);
    }

    // The chooser: every catalog table as a cascade-delete tree with a checkbox each.
    [HttpGet("tables")]
    public async Task<IActionResult> Tables(bool counts, CancellationToken ct)
    {
        var shown = await _selection.GetShownClrNamesAsync(ct);
        var model = new DataAdminTablesViewModel
        {
            ShowCounts = counts,
            SelectedCount = shown.Count,
            TotalCount = _catalog.Models.Count(table => !table.IsHidden),
        };
        foreach (var root in _catalog.Forest.Roots)
        {
            model.Roots.AddRange(await BuildNodesAsync(root, "", shown, counts, ct));
        }
        return View(model);
    }

    [HttpPost("tables")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Tables(string[]? show, CancellationToken ct)
    {
        var wanted = show ?? Array.Empty<string>();
        await _selection.SetShownAsync(wanted, ct);
        var shown = await _selection.GetShownClrNamesAsync(ct);

        var actor = SuperAdminOnlyAttribute.GetUser(HttpContext);
        _logger.LogWarning(
            "Data Admin: {Actor} set the shown tables to [{Tables}]",
            actor.UserName,
            string.Join(", ", shown.OrderBy(name => name, StringComparer.Ordinal)));

        TempData["DataAdminMessage"] = shown.Count == 0
            ? "No tables are shown."
            : $"Showing {shown.Count} table{(shown.Count == 1 ? "" : "s")}.";
        return RedirectToAction(nameof(Index));
    }

    // A table is reachable only when it is in the catalog AND currently shown; otherwise the
    // caller answers 404, which is also what a policy-hidden table gets.
    private async Task<DataAdminModel?> ResolveShownAsync(string? slug, CancellationToken ct)
    {
        var table = _catalog.Find(slug);
        return table is not null && await _selection.IsShownAsync(table, ct) ? table : null;
    }

    private DataAdminModel RootOf(DataAdminModel table)
    {
        var node = _catalog.Forest.ByItem[table];
        while (node.Parent is not null)
        {
            node = node.Parent;
        }
        return node.Item;
    }

    // A Hidden table is not rendered; its children (none today) are hoisted to its level so they
    // stay reachable on the page.
    private async Task<List<DataAdminTableNode>> BuildNodesAsync(
        DataAdminCascadeForest<DataAdminModel>.Node node,
        string parentPath,
        IReadOnlySet<string> shown,
        bool counts,
        CancellationToken ct)
    {
        var table = node.Item;
        if (table.IsHidden)
        {
            var hoisted = new List<DataAdminTableNode>();
            foreach (var child in node.Children)
            {
                hoisted.AddRange(await BuildNodesAsync(child, parentPath, shown, counts, ct));
            }
            return hoisted;
        }

        var path = parentPath.Length == 0 ? table.ClrName : parentPath + "/" + table.ClrName;
        var built = new DataAdminTableNode
        {
            ClrName = table.ClrName,
            DisplayName = table.DisplayName,
            TableName = table.TableName,
            Slug = table.Slug,
            Path = path,
            Depth = node.Depth,
            Selected = shown.Contains(table.ClrName),
            ReadOnly = table.IsReadOnly,
            NoCreate = !table.CanCreate,
            NoDelete = !table.CanDelete,
            DiscordSideEffect = table.Policy.DiscordSideEffect,
            RowCount = counts ? await table.CountAsync(_db, ct) : null,
            AlsoDeletedWith = node.AlsoDeletedWith.Select(other => other.DisplayName).ToList(),
            CascadeDescendants = node.CascadeDescendants.Select(other => other.DisplayName).ToList(),
        };
        foreach (var child in node.Children)
        {
            built.Children.AddRange(await BuildNodesAsync(child, path, shown, counts, ct));
        }
        return new List<DataAdminTableNode> { built };
    }
}
