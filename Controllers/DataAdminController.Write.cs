using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Writes: create and edit. Every POST re-checks the table's capability server-side (the buttons
// are hidden when a policy forbids an action, but the check here is the one that counts).
public sealed partial class DataAdminController
{
    [HttpGet("{slug:regex(^[a-z0-9-]+$)}/create")]
    public async Task<IActionResult> Create(string slug, CancellationToken ct)
    {
        var table = await ResolveShownAsync(slug, ct);
        if (table is null)
        {
            return NotFound();
        }
        if (!table.CanCreate)
        {
            return Forbid();
        }

        var model = await _editor.BuildFormAsync(table, entity: null, posted: null, Array.Empty<DataAdminFieldError>(), ct);
        return View("Edit", model);
    }

    [HttpPost("{slug:regex(^[a-z0-9-]+$)}/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string slug, [FromForm] Dictionary<string, string?> values, CancellationToken ct)
    {
        var table = await ResolveShownAsync(slug, ct);
        if (table is null)
        {
            return NotFound();
        }

        var result = await _editor.CreateAsync(table, values, SuperAdminOnlyAttribute.GetUser(HttpContext), ct);
        if (result.Forbidden)
        {
            return Forbid();
        }
        if (result.Succeeded && result.Entity is not null)
        {
            TempData["DataAdminMessage"] = $"{table.DisplayName} created.";
            return RedirectToAction(nameof(Details), new { slug, id = table.KeyToString(table.KeyOf(result.Entity)) });
        }

        var model = await _editor.BuildFormAsync(table, entity: null, values, result.Errors, ct);
        return View("Edit", model);
    }

    [HttpGet("{slug:regex(^[a-z0-9-]+$)}/{id}/edit")]
    public async Task<IActionResult> Edit(string slug, string id, CancellationToken ct)
    {
        var table = await ResolveShownAsync(slug, ct);
        if (table is null || !table.TryParseKey(id, out var key))
        {
            return NotFound();
        }
        if (!table.CanEdit)
        {
            return Forbid();
        }
        var entity = await table.FindAsync(_db, key, track: false, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var model = await _editor.BuildFormAsync(table, entity, posted: null, Array.Empty<DataAdminFieldError>(), ct);
        return View(model);
    }

    [HttpPost("{slug:regex(^[a-z0-9-]+$)}/{id}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string slug, string id, [FromForm] Dictionary<string, string?> values, CancellationToken ct)
    {
        var table = await ResolveShownAsync(slug, ct);
        if (table is null || !table.TryParseKey(id, out var key))
        {
            return NotFound();
        }

        var result = await _editor.UpdateAsync(table, key, values, SuperAdminOnlyAttribute.GetUser(HttpContext), ct);
        if (result.Forbidden)
        {
            return Forbid();
        }
        if (result.NotFound || result.Entity is null)
        {
            return NotFound();
        }
        if (result.Succeeded)
        {
            TempData["DataAdminMessage"] = $"{table.DisplayName} #{id} saved.";
            return RedirectToAction(nameof(Details), new { slug, id });
        }

        var model = await _editor.BuildFormAsync(table, result.Entity, values, result.Errors, ct);
        return View(model);
    }

    // The confirmation page: the cascade-impact tree for this row.
    [HttpGet("{slug:regex(^[a-z0-9-]+$)}/{id}/delete")]
    public async Task<IActionResult> Delete(string slug, string id, CancellationToken ct)
    {
        var table = await ResolveShownAsync(slug, ct);
        if (table is null || !table.TryParseKey(id, out var key))
        {
            return NotFound();
        }
        if (!table.CanDelete)
        {
            return Forbid();
        }
        var entity = await table.FindAsync(_db, key, track: false, ct);
        if (entity is null)
        {
            return NotFound();
        }

        var impact = await DataAdminCascadePreview.ComputeAsync(_db, table, entity, ct);
        return View(ToViewModel(table, impact));
    }

    // The editor recomputes the impact and refuses a delete the database would refuse, so a
    // page left open while the data changed cannot slip past the preview.
    [HttpPost("{slug:regex(^[a-z0-9-]+$)}/{id}/delete")]
    [ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(string slug, string id, CancellationToken ct)
    {
        var table = await ResolveShownAsync(slug, ct);
        if (table is null || !table.TryParseKey(id, out var key))
        {
            return NotFound();
        }

        var result = await _editor.DeleteAsync(table, key, SuperAdminOnlyAttribute.GetUser(HttpContext), ct);
        if (result.Forbidden)
        {
            return Forbid();
        }
        if (result.NotFound)
        {
            return NotFound();
        }
        if (!result.Succeeded)
        {
            TempData["DataAdminError"] = string.Join(" ", result.Errors.Select(e => e.Message));
            return RedirectToAction(nameof(Delete), new { slug, id });
        }

        TempData["DataAdminMessage"] = $"{table.DisplayName} #{id} deleted.";
        return RedirectToAction(nameof(List), new { slug });
    }

    private static DataAdminDeleteViewModel ToViewModel(DataAdminModel table, DataAdminCascadeImpact impact)
    {
        var model = new DataAdminDeleteViewModel
        {
            Slug = table.Slug,
            DisplayName = table.DisplayName,
            Key = impact.Key,
            Label = impact.Label,
            IsBlocked = impact.IsBlocked,
            IsTruncated = impact.IsTruncated,
            DeletedRows = impact.DeletedRows,
            DiscordSideEffect = table.Policy.DiscordSideEffect,
        };
        foreach (var child in impact.Children)
        {
            model.Children.Add(ToNode(child));
        }
        foreach (var (dependent, rows) in impact.DeletedTotals.OrderBy(pair => pair.Key.DisplayName, StringComparer.Ordinal))
        {
            model.Totals.Add(new DataAdminImpactTotal(dependent.DisplayName, rows));
        }
        return model;
    }

    private static DataAdminImpactNode ToNode(DataAdminCascadeImpact.Node node)
    {
        var built = new DataAdminImpactNode
        {
            DisplayName = node.Table.DisplayName,
            Slug = node.Table.Slug,
            ForeignKeyDisplayName = node.Table.FindColumn(node.ForeignKeyColumn)?.DisplayName ?? node.ForeignKeyColumn,
            Effect = node.Effect switch
            {
                DataAdminCascadeImpact.Effect.Deleted => "deleted",
                DataAdminCascadeImpact.Effect.Unlinked => "unlinked",
                _ => "blocked",
            },
            Count = node.Count,
            AlreadyCounted = node.AlreadyCounted,
            Overflow = node.Overflow,
            DepthCapped = node.DepthCapped,
        };
        foreach (var child in node.Children)
        {
            built.Children.Add(ToNode(child));
        }
        return built;
    }
}
