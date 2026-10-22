using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// DKP import (Excel/CSV): a linkshell migrating from another tracker brings its existing DKP in.
// The JSON twin of LinkshellController's DownloadDkpTemplate / PreviewDkpImport / CommitDkpImport,
// on the same DkpImportService, for the Configurations tab's website-only import card (uploading a
// file and reviewing a table is not something the Discord iframe offers).
//
// Two steps, as on the Razor page: preview parses the upload and classifies every row (update /
// create / ambiguous) without changing anything and hands the parsed rows back; commit applies
// those rows. Gated on CanCustomizeLinkshell, where the Razor page checked the Leader/Officer rank;
// the move to the Activity's permission flags is the drift this whole switch accepts.
public sealed partial class ActivityDataController
{
    // A blank sample .xlsx with the exact columns the importer expects.
    [HttpGet("dkp-import/template")]
    public async Task<IActionResult> DownloadDkpImportTemplateAsync(CancellationToken cancellationToken)
    {
        if (await ResolveAppUserAsync(cancellationToken) is null)
        {
            return Unauthorized(new { error = "Sign in to download the DKP template." });
        }

        return File(SampleDkpTemplateWorkbook.Build(), SampleDkpTemplateWorkbook.ContentType, SampleDkpTemplateWorkbook.FileName);
    }

    [HttpPost("linkshells/{linkshellId:int}/dkp-import/preview")]
    [RequestSizeLimit(8_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 8_000_000)]
    public async Task<IActionResult> PreviewDkpImportAsync(
        int linkshellId,
        IFormFile? file,
        [FromServices] DkpImportService import,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to import DKP." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, role => role.CanCustomizeLinkshell, cancellationToken)) return Forbid();

        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "Choose an .xlsx or .csv file to import." });
        }

        List<DkpImportRow> parsed;
        try
        {
            await using var stream = file.OpenReadStream();
            parsed = import.ParseImport(XlsxImportReader.Read(stream, file.FileName));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Could not read the file: {ex.Message}" });
        }

        if (parsed.Count == 0)
        {
            return BadRequest(new { error = "No member rows found. Make sure the sheet has a Member Name column and Current DKP values." });
        }

        var preview = await import.BuildPreviewAsync(linkshellId, parsed, file.FileName, cancellationToken);
        return Ok(new ActivityDkpImportPreviewResponse(
            preview.SourceLabel,
            preview.UpdateCount,
            preview.CreateCount,
            preview.AmbiguousCount,
            preview.ClampedCount,
            preview.Rows
                .Select(row => new ActivityDkpImportPreviewRowDto(
                    row.Name, row.Alt1, row.Alt2,
                    // A string, not the enum's number: no JsonStringEnumConverter is registered.
                    row.Kind.ToString(),
                    row.MatchedExistingName,
                    row.OldCurrent, row.NewCurrent, row.NewTotal, row.NewSpent,
                    row.Note))
                .ToList(),
            parsed));
    }

    [HttpPost("linkshells/{linkshellId:int}/dkp-import/commit")]
    public async Task<IActionResult> CommitDkpImportAsync(
        int linkshellId,
        [FromBody] ActivityDkpImportCommitRequest request,
        [FromServices] DkpImportService import,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to import DKP." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, role => role.CanCustomizeLinkshell, cancellationToken)) return Forbid();

        if (request.Rows is not { Count: > 0 })
        {
            return BadRequest(new { error = "The import session expired — please re-upload the file." });
        }

        var result = await import.CommitAsync(linkshellId, request.Rows, request.SourceLabel ?? "DKP import", cancellationToken);

        var parts = new List<string>();
        if (result.Updated > 0) { parts.Add($"{result.Updated} updated"); }
        if (result.Created > 0) { parts.Add($"{result.Created} new member{(result.Created == 1 ? "" : "s")} created"); }
        if (result.Ambiguous > 0) { parts.Add($"{result.Ambiguous} skipped (ambiguous)"); }
        var message = parts.Count > 0
            ? $"DKP import complete — {string.Join(", ", parts)}."
            : "DKP import complete — no changes were needed.";

        return Ok(new ActivityDkpImportCommitResponse(message, result.Updated, result.Created, result.Ambiguous));
    }
}

// Kind is "Update", "Create", "Ambiguous" or "Skipped" (DkpImportRowKind).
public sealed record ActivityDkpImportPreviewRowDto(
    string Name, string? Alt1, string? Alt2,
    string Kind,
    string? MatchedExistingName,
    double OldCurrent, double NewCurrent,
    double NewTotal, double NewSpent,
    string? Note);

// Rows is the parsed upload, handed back so the client can send exactly it to commit, the way the
// Razor preview page round-tripped it through a hidden field.
public sealed record ActivityDkpImportPreviewResponse(
    string SourceLabel,
    int UpdateCount,
    int CreateCount,
    int AmbiguousCount,
    int ClampedCount,
    IReadOnlyList<ActivityDkpImportPreviewRowDto> Preview,
    IReadOnlyList<DkpImportRow> Rows);

public sealed record ActivityDkpImportCommitRequest(List<DkpImportRow>? Rows, string? SourceLabel);

public sealed record ActivityDkpImportCommitResponse(string Message, int Updated, int Created, int Ambiguous);
