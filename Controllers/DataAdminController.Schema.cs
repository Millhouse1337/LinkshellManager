using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// The schema diagram page of the Data Admin section. It reads the model's structure (the catalog's
// schema graph) and which tables are shown, and nothing else: no table is queried for rows here.
public sealed partial class DataAdminController
{
    // The whole database as a picture: structure only (tables, columns, foreign keys), never rows.
    // "schema" is a literal segment, so it wins over {slug}; it is also a reserved slug
    // (DataAdminDefaults.ReservedSlugs), so no table can ever be hidden behind this page.
    [HttpGet("schema")]
    public async Task<IActionResult> Schema(CancellationToken ct)
    {
        var graph = _catalog.Schema;
        var shown = await _selection.GetShownClrNamesAsync(ct);

        return View(new DataAdminSchemaViewModel
        {
            DatabaseName = DatabaseName(),
            TableCount = graph.Tables.Count,
            ColumnCount = graph.ColumnCount,
            RelationshipCount = graph.Relationships.Count,
            // Only a table with a slug has a rows page, so only those can get a "Browse rows" link.
            // A stale name in the selection (a table removed since) matches nothing and drops out.
            ShownTableIds = graph.Tables
                .Where(table => table.Slug is not null && shown.Contains(table.Id))
                .Select(table => table.Id)
                .ToList(),
            DiagramJson = graph.Json,
        });
    }

    // The name in the page's header. Reading it off the connection object does not open a
    // connection; the in-memory provider (tests) has no connection at all, hence the guard.
    private string DatabaseName() =>
        _db.Database.IsRelational() ? _db.Database.GetDbConnection().Database : "in-memory";
}
