using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// DKP import (Excel/CSV): a linkshell migrating from another tracker brings its existing DKP in.
// Runs on DkpImportService, for the DKP tab's Import DKP view, on the website and in Discord
// (the file arrives as base64 JSON, since the Discord iframe can't send a multipart upload).
//
// Two steps: preview parses the upload and classifies every row (update / create / ambiguous)
// without changing anything and hands the parsed rows back; commit applies those rows. Gated on
// CanCustomizeLinkshell.
public sealed partial class ActivityDkpController
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

    // The file arrives as base64 in JSON, not a multipart upload: the Discord iframe can't send
    // multipart (the banner upload does the same), and one path serves both hosts.
    private const int MaxImportFileBytes = 8_000_000;

    [HttpPost("linkshells/{linkshellId:int}/dkp-import/preview")]
    [RequestSizeLimit(11_000_000)] // 8 MB of file is ~10.7 MB once base64-encoded
    public async Task<IActionResult> PreviewDkpImportAsync(
        int linkshellId,
        [FromBody] ActivityDkpImportUploadRequest request,
        [FromServices] DkpImportService import,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to import DKP." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, role => role.CanCustomizeLinkshell, cancellationToken)) return Forbid();

        if (request is null || string.IsNullOrWhiteSpace(request.DataBase64))
        {
            return BadRequest(new { error = "Choose an .xlsx or .csv file to import." });
        }

        // Accept either a raw base64 string or a full "data:...;base64,xxxx" URL.
        var payload = request.DataBase64;
        var comma = payload.IndexOf(',');
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
        {
            payload = payload[(comma + 1)..];
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(payload); }
        catch (FormatException) { return BadRequest(new { error = "The file didn't arrive intact — please choose it again." }); }

        if (bytes.Length == 0 || bytes.Length > MaxImportFileBytes)
        {
            return BadRequest(new { error = "The file must be between 1 byte and 8 MB." });
        }

        // Only the name's extension matters (it picks .xlsx or .csv parsing); never a path.
        var fileName = Path.GetFileName(request.FileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "import.xlsx";
        }

        List<DkpImportRow> parsed;
        try
        {
            using var stream = new MemoryStream(bytes);
            parsed = import.ParseImport(XlsxImportReader.Read(stream, fileName));
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = $"Could not read the file: {ex.Message}" });
        }

        if (parsed.Count == 0)
        {
            return BadRequest(new { error = "No member rows found. Make sure the sheet has a Member Name column and Current DKP values." });
        }

        var preview = await import.BuildPreviewAsync(linkshellId, parsed, fileName, cancellationToken);
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

// Rows is the parsed upload, handed back so the client can send exactly it to commit.
public sealed record ActivityDkpImportPreviewResponse(
    string SourceLabel,
    int UpdateCount,
    int CreateCount,
    int AmbiguousCount,
    int ClampedCount,
    IReadOnlyList<ActivityDkpImportPreviewRowDto> Preview,
    IReadOnlyList<DkpImportRow> Rows);

public sealed record ActivityDkpImportCommitRequest(List<DkpImportRow>? Rows, string? SourceLabel);

// The spreadsheet to preview: its name (for the extension) and its bytes as base64, raw or as a
// "data:...;base64," URL.
public sealed record ActivityDkpImportUploadRequest(string? FileName, string? DataBase64);

public sealed record ActivityDkpImportCommitResponse(string Message, int Updated, int Created, int Ambiguous);
