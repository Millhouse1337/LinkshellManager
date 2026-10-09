using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Read-only pages of the Data Admin section: the list page (and, later, the details page).
public sealed partial class DataAdminController
{
    // /data-admin/{slug}?q=&sort=&desc=&page=&f.<Column>=  -- see DataAdminListQuery.
    // No regex constraint on {slug}: literal segments (create, edit, delete) already win over
    // a parameter, and square brackets inside an attribute route template are read as [token]
    // replacements, which crashed startup once. The catalog lookup 404s anything that is not a slug.
    [HttpGet("{slug}")]
    public async Task<IActionResult> List(string slug, CancellationToken ct)
    {
        var table = Resolve(slug);
        if (table is null)
        {
            return NotFound();
        }

        var query = DataAdminListQuery.Parse(Request.Query);
        var model = await _queries.ListAsync(table, query, ct);
        return View(model);
    }

    // /data-admin/{slug}/{id}: the id is parsed by the table's key type, so "abc" on an int key is a 404.
    [HttpGet("{slug}/{id}")]
    public async Task<IActionResult> Details(string slug, string id, CancellationToken ct)
    {
        var table = Resolve(slug);
        if (table is null || !table.TryParseKey(id, out var key))
        {
            return NotFound();
        }

        var model = await _queries.DetailsAsync(table, key, ct);
        return model is null ? NotFound() : View(model);
    }
}
