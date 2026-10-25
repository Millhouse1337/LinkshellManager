using System.Text.Json;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Data Admin as JSON, for the Activity's website-only Data Admin tab, on the catalog, query
// service and editor, answering with the Data Admin view models (they are already the safe
// projection: display text, never entities). A table resolves through the catalog and an unknown
// or policy-hidden slug is a 404; DataAdminPolicy is the only protection, and every write
// re-checks the table's capability in the editor.
//
// Gated structurally on the class ([Authorize] + [SuperAdminOnly]), with [ValidateAntiForgeryToken]
// on every POST on top of the global filter (DataAdminApiControllerShapeTests).
[ApiController]
[Authorize]
[SuperAdminOnly]
[Route("api/data-admin")]
public sealed class DataAdminApiController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly DataAdminCatalog _catalog;
    private readonly DataAdminQueryService _queries;
    private readonly DataAdminEditor _editor;

    public DataAdminApiController(
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

    // The landing view: the schema diagram's header numbers plus the graph itself (structure only,
    // never rows), in the shape wwwroot/js/data-admin-schema.js reads.
    [HttpGet("schema")]
    public IActionResult GetSchema()
    {
        var graph = _catalog.Schema;
        using var diagram = JsonDocument.Parse(graph.Json);
        return Ok(new DataAdminSchemaDto(
            _db.Database.IsRelational() ? _db.Database.GetDbConnection().Database : "in-memory",
            graph.Tables.Count,
            graph.ColumnCount,
            graph.Relationships.Count,
            diagram.RootElement.Clone()));
    }

    // ?q=&sort=&desc=&page=&f.<Column>= — see DataAdminListQuery.
    [HttpGet("tables/{slug}")]
    public async Task<IActionResult> ListAsync(string slug, CancellationToken ct)
    {
        var table = Resolve(slug);
        if (table is null) return TableNotFound();

        return Ok(await _queries.ListAsync(table, DataAdminListQuery.Parse(Request.Query), ct));
    }

    [HttpGet("tables/{slug}/{id}")]
    public async Task<IActionResult> DetailsAsync(string slug, string id, CancellationToken ct)
    {
        var table = Resolve(slug);
        if (table is null || !table.TryParseKey(id, out var key)) return TableNotFound();

        var model = await _queries.DetailsAsync(table, key, ct);
        return model is null ? RowNotFound() : Ok(model);
    }

    // The empty create form.
    [HttpGet("tables/{slug}/form")]
    public async Task<IActionResult> CreateFormAsync(string slug, CancellationToken ct)
    {
        var table = Resolve(slug);
        if (table is null) return TableNotFound();
        if (!table.CanCreate) return Forbid();

        return Ok(await _editor.BuildFormAsync(table, entity: null, posted: null, Array.Empty<DataAdminFieldError>(), ct));
    }

    [HttpGet("tables/{slug}/{id}/form")]
    public async Task<IActionResult> EditFormAsync(string slug, string id, CancellationToken ct)
    {
        var table = Resolve(slug);
        if (table is null || !table.TryParseKey(id, out var key)) return TableNotFound();
        if (!table.CanEdit) return Forbid();

        var entity = await table.FindAsync(_db, key, track: false, ct);
        if (entity is null) return RowNotFound();

        return Ok(await _editor.BuildFormAsync(table, entity, posted: null, Array.Empty<DataAdminFieldError>(), ct));
    }

    // Created: the new row's key. Refused: 422 with the form rebuilt around the posted values and
    // its errors.
    [HttpPost("tables/{slug}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAsync(string slug, [FromBody] DataAdminValuesRequest request, CancellationToken ct)
    {
        var table = Resolve(slug);
        if (table is null) return TableNotFound();

        var values = request.Values ?? new Dictionary<string, string?>();
        var result = await _editor.CreateAsync(table, values, SuperAdminOnlyAttribute.GetUser(HttpContext), ct);
        if (result.Forbidden) return Forbid();
        if (result.Succeeded && result.Entity is not null)
        {
            return Ok(new DataAdminSavedDto(table.KeyToString(table.KeyOf(result.Entity)), $"{table.DisplayName} created."));
        }

        return FormRefused(await _editor.BuildFormAsync(table, entity: null, values, result.Errors, ct));
    }

    [HttpPost("tables/{slug}/{id}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAsync(string slug, string id, [FromBody] DataAdminValuesRequest request, CancellationToken ct)
    {
        var table = Resolve(slug);
        if (table is null || !table.TryParseKey(id, out var key)) return TableNotFound();

        var values = request.Values ?? new Dictionary<string, string?>();
        var result = await _editor.UpdateAsync(table, key, values, SuperAdminOnlyAttribute.GetUser(HttpContext), ct);
        if (result.Forbidden) return Forbid();
        if (result.NotFound || result.Entity is null) return RowNotFound();
        if (result.Succeeded)
        {
            return Ok(new DataAdminSavedDto(id, $"{table.DisplayName} #{id} saved."));
        }

        return FormRefused(await _editor.BuildFormAsync(table, result.Entity, values, result.Errors, ct));
    }

    // The confirmation: what else goes with the row, what is unlinked, what blocks the delete.
    [HttpGet("tables/{slug}/{id}/delete-preview")]
    public async Task<IActionResult> DeletePreviewAsync(string slug, string id, CancellationToken ct)
    {
        var table = Resolve(slug);
        if (table is null || !table.TryParseKey(id, out var key)) return TableNotFound();
        if (!table.CanDelete) return Forbid();

        var entity = await table.FindAsync(_db, key, track: false, ct);
        if (entity is null) return RowNotFound();

        var impact = await DataAdminCascadePreview.ComputeAsync(_db, table, entity, ct);
        return Ok(DataAdminImpactMapper.ToViewModel(table, impact));
    }

    // The editor recomputes the impact and refuses a delete the database would refuse, so a preview
    // left open while the data changed cannot slip past it (409 with the reason).
    [HttpPost("tables/{slug}/{id}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAsync(string slug, string id, CancellationToken ct)
    {
        var table = Resolve(slug);
        if (table is null || !table.TryParseKey(id, out var key)) return TableNotFound();

        var result = await _editor.DeleteAsync(table, key, SuperAdminOnlyAttribute.GetUser(HttpContext), ct);
        if (result.Forbidden) return Forbid();
        if (result.NotFound) return RowNotFound();
        if (!result.Succeeded)
        {
            return Conflict(new { error = string.Join(" ", result.Errors.Select(e => e.Message)) });
        }

        return Ok(new DataAdminSavedDto(id, $"{table.DisplayName} #{id} deleted."));
    }

    // 422: a one-line summary for the app's error banner, plus the form rebuilt around the posted
    // values with each field's error, which the app shows in place of the one it sent.
    private UnprocessableEntityObjectResult FormRefused(DataAdminEditViewModel form) => UnprocessableEntity(new
    {
        error = form.FormErrors.Count > 0 ? string.Join(" ", form.FormErrors) : "Check the highlighted fields.",
        form
    });

    private DataAdminModel? Resolve(string? slug) =>
        _catalog.Find(slug) is { IsHidden: false } table ? table : null;

    private NotFoundObjectResult TableNotFound() => NotFound(new { error = "No such table." });

    private NotFoundObjectResult RowNotFound() => NotFound(new { error = "That row doesn't exist (any more)." });
}

// Diagram is DataAdminSchemaGraph.Json as-is: the names data-admin-schema.js reads.
public sealed record DataAdminSchemaDto(string DatabaseName, int TableCount, int ColumnCount, int RelationshipCount, JsonElement Diagram);

// Column name -> the form's string value.
public sealed record DataAdminValuesRequest(Dictionary<string, string?>? Values);

public sealed record DataAdminSavedDto(string Key, string Message);
