using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Read-only pages of the Data Admin section: the list page (and, later, the details page).
public sealed partial class DataAdminController
{
    // /data-admin/{slug}?q=&sort=&desc=&page=&f.<Column>=  -- see DataAdminListQuery.
    // The slug constraint keeps the literal segments (tables, create, ...) unambiguous.
    [HttpGet("{slug:regex(^[a-z0-9-]+$)}")]
    public async Task<IActionResult> List(string slug, CancellationToken ct)
    {
        var table = await ResolveShownAsync(slug, ct);
        if (table is null)
        {
            return NotFound();
        }

        var query = DataAdminListQuery.Parse(Request.Query);
        var shown = await _selection.GetShownClrNamesAsync(ct);
        var model = await _queries.ListAsync(table, query, shown, ct);
        return View(model);
    }

    // /data-admin/{slug}/{id}: the id is parsed by the table's key type, so "abc" on an int key is a 404.
    [HttpGet("{slug:regex(^[a-z0-9-]+$)}/{id}")]
    public async Task<IActionResult> Details(string slug, string id, CancellationToken ct)
    {
        var table = await ResolveShownAsync(slug, ct);
        if (table is null || !table.TryParseKey(id, out var key))
        {
            return NotFound();
        }

        var shown = await _selection.GetShownClrNamesAsync(ct);
        var model = await _queries.DetailsAsync(table, key, shown, ct);
        return model is null ? NotFound() : View(model);
    }
}
