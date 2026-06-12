using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public sealed class LinkshellSheetController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly GoogleSheetsSyncService _sheets;
    private readonly GoogleOAuthService _oauth;
    private readonly GoogleSheetsOptions _options;
    private readonly DkpTemplateSheetService _template;
    private readonly SheetTemplateSyncQueue _templateSync;
    private readonly TimeZoneConversionService _timeZones;

    public LinkshellSheetController(
        ApplicationDbContext db,
        UserManager<AppUser> userManager,
        GoogleSheetsSyncService sheets,
        GoogleOAuthService oauth,
        IOptions<GoogleSheetsOptions> options,
        DkpTemplateSheetService template,
        SheetTemplateSyncQueue templateSync,
        TimeZoneConversionService timeZones)
    {
        _db = db;
        _userManager = userManager;
        _sheets = sheets;
        _oauth = oauth;
        _options = options.Value;
        _template = template;
        _templateSync = templateSync;
        _timeZones = timeZones;
    }

    // Import page: connect Google (step 1) + import existing DKP from an uploaded
    // spreadsheet (step 2). The live-sync toggle lives in this page's header.
    [HttpGet("/linkshells/{linkshellId:int}/sheet")]
    public async Task<IActionResult> Index(int linkshellId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var viewModel = await BuildViewModelAsync(linkshellId);
        if (viewModel is null) return NotFound();
        ApplyConnectedAtLocal(viewModel, user);
        return View(viewModel);
    }

    // Export page: create/connect the dedicated Google Sheet and push DKP into it.
    [HttpGet("/linkshells/{linkshellId:int}/sheet/export")]
    public async Task<IActionResult> Export(int linkshellId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var viewModel = await BuildViewModelAsync(linkshellId);
        if (viewModel is null) return NotFound();
        ApplyConnectedAtLocal(viewModel, user);
        return View(viewModel);
    }

    // ConnectedAt is stored UTC; surface a viewer-local copy so the "Connected as
    // ... since ..." line reads in the user's profile timezone instead of UTC.
    // Falls back to UTC silently when the user has no zone configured.
    private void ApplyConnectedAtLocal(LinkshellSheetViewModel viewModel, AppUser user)
    {
        if (!viewModel.ConnectedAt.HasValue) return;
        var local = _timeZones.ToUserTime(viewModel.ConnectedAt, user.TimeZone);
        if (local.HasValue && !string.IsNullOrWhiteSpace(user.TimeZone))
        {
            viewModel.ConnectedAtUserLocal = local;
            viewModel.ConnectedAtTimeZoneLabel = user.TimeZone;
        }
    }

    private async Task<LinkshellSheetViewModel?> BuildViewModelAsync(int linkshellId)
    {
        var linkshell = await _db.Linkshells.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkshellId);
        if (linkshell is null) return null;

        return new LinkshellSheetViewModel
        {
            LinkshellId = linkshell.Id,
            LinkshellName = linkshell.LinkshellName,
            SpreadsheetId = linkshell.GoogleSpreadsheetId,
            GoogleSheetAppCreated = linkshell.GoogleSheetAppCreated,
            IsOAuthConfigured = _oauth.IsConfigured,
            IsOAuthConnected = !string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc),
            ConnectedGoogleEmail = linkshell.GoogleOAuthUserEmail,
            ConnectedAt = linkshell.GoogleOAuthConnectedAt,
            DkpTemplateTabName = DkpTemplateSheetService.ResolveTabName(linkshell.DkpTemplateTabName),
            LiveSyncEnabled = linkshell.SheetTemplateSyncEnabled,
        };
    }

    // Toggle live sync (push-only): when ON, the "LSM DKP" tab is auto-refreshed
    // whenever DKP changes. Enabling while connected kicks off an immediate push
    // so the sheet reflects the current state right away.
    [HttpPost("/linkshells/{linkshellId:int}/sheet/live-sync-toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleLiveSync(int linkshellId, [FromForm] bool enabled, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);
        if (linkshell is null) return NotFound();

        linkshell.SheetTemplateSyncEnabled = enabled;
        await _db.SaveChangesAsync(cancellationToken);

        if (enabled
            && !string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId)
            && !string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            // Push current state now (debounced background export) so the tab is
            // immediately current instead of waiting for the next DKP change.
            _templateSync.Enqueue(linkshellId);
        }

        TempData["SheetConfigSuccess"] = enabled
            ? "Live sync is ON — the \"LSM DKP\" tab now refreshes automatically whenever DKP changes."
            : "Live sync is OFF — the sheet only updates when you click \"Export DKP to template\".";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/sheet/connect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Connect(int linkshellId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        if (!_oauth.IsConfigured)
        {
            TempData["SheetConfigError"] = "Google OAuth is not configured on the server.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        var redirectUri = BuildCallbackUri();
        var url = _oauth.BuildAuthorizationUrl(linkshellId, user.Id, redirectUri);
        return Redirect(url);
    }

    [HttpGet("/signin-google-sheets")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            TempData["SheetConfigError"] = $"Google denied the request: {error}";
            return RedirectToAction("Index", "Home");
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            TempData["SheetConfigError"] = "OAuth callback was missing code or state.";
            return RedirectToAction("Index", "Home");
        }

        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        try
        {
            // Authorize BEFORE exchanging the code or persisting any token onto
            // the shared linkshell row: resolve the linkshell from the signed
            // state, check manage permission, only then complete the exchange.
            var linkshellId = _oauth.ResolveLinkshellFromState(state, user.Id);
            if (!await CanManageAsync(user.Id, linkshellId))
            {
                return Forbid();
            }
            await _oauth.HandleCallbackAsync(code, state, user.Id, BuildCallbackUri(), cancellationToken);
            TempData["SheetConfigSuccess"] = "Google account connected.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }
        catch (GoogleOAuthException ex)
        {
            TempData["SheetConfigError"] = ex.Message;
            return RedirectToAction("Index", "Home");
        }
    }

    [HttpPost("/linkshells/{linkshellId:int}/sheet/disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        await _oauth.MarkDisconnectedAsync(linkshellId, cancellationToken);
        TempData["SheetConfigSuccess"] = "Google account disconnected. Sync paused until you reconnect.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // Saves the optional template tab name. The spreadsheet itself is no longer
    // pasted in — under the drive.file scope the app can only use a sheet it
    // created (see CreateSheet), so the id is set there, not here.
    [HttpPost("/linkshells/{linkshellId:int}/sheet/config")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveConfig(int linkshellId,
        [FromForm] string? dkpTemplateTabName)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId);
        if (linkshell is null) return NotFound();

        linkshell.DkpTemplateTabName = string.IsNullOrWhiteSpace(dkpTemplateTabName) ? null : dkpTemplateTabName.Trim();

        await _db.SaveChangesAsync();
        TempData["SheetConfigSuccess"] = "Template tab name saved.";
        return RedirectToAction(nameof(Export), new { linkshellId });
    }

    // Creates a dedicated "LSM DKP" spreadsheet owned by the connected Google
    // account and links it. This is the only way to obtain a sheet under the
    // drive.file scope (the app can't open arbitrary sheets the user owns), so
    // it replaces the old "paste a spreadsheet id" flow. Immediately exports the
    // current DKP into the template tab so the new sheet isn't empty.
    [HttpPost("/linkshells/{linkshellId:int}/sheet/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSheet(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);
        if (linkshell is null) return NotFound();
        if (string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            TempData["SheetConfigError"] = "Connect a Google account first.";
            return RedirectToAction(nameof(Export), new { linkshellId });
        }

        try
        {
            var title = string.IsNullOrWhiteSpace(linkshell.LinkshellName)
                ? "LSM DKP"
                : $"LSM DKP — {linkshell.LinkshellName}";
            var newId = await _sheets.CreateSpreadsheetAsync(linkshellId, title, cancellationToken);

            linkshell.GoogleSpreadsheetId = newId;
            linkshell.GoogleSheetAppCreated = true;
            await _db.SaveChangesAsync(cancellationToken);
            _sheets.InvalidateCache(linkshellId);

            // Populate the template tab right away. Non-fatal: the sheet exists
            // and is linked either way, and the user can Export manually.
            try
            {
                var export = await _template.ExportAsync(linkshellId, cancellationToken);
                TempData["SheetConfigSuccess"] =
                    $"Created a dedicated \"LSM DKP\" sheet and exported {export.MemberCount} member(s) into it.";
            }
            catch (Exception)
            {
                TempData["SheetConfigSuccess"] =
                    "Created a dedicated \"LSM DKP\" sheet. Click \"Export DKP to template\" to populate it.";
            }
        }
        catch (GoogleOAuthRevokedException)
        {
            TempData["SheetConfigError"] = "Google rejected the saved connection — reconnect the Google account.";
        }
        catch (Exception ex)
        {
            TempData["SheetConfigError"] = $"Could not create the sheet: {ex.Message}";
        }
        return RedirectToAction(nameof(Export), new { linkshellId });
    }

    // Export the linkshell's DKP into the styled generic template tab on the
    // connected sheet (creates/refreshes the tab).
    [HttpPost("/linkshells/{linkshellId:int}/sheet/export-template")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportTemplate(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var linkshell = await _db.Linkshells.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);
        if (string.IsNullOrWhiteSpace(linkshell?.GoogleSpreadsheetId))
        {
            TempData["SheetImportError"] = "Create your DKP sheet before exporting.";
            return RedirectToAction(nameof(Export), new { linkshellId });
        }
        if (string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            TempData["SheetImportError"] = "Connect a Google account before exporting.";
            return RedirectToAction(nameof(Export), new { linkshellId });
        }

        try
        {
            var result = await _template.ExportAsync(linkshellId, cancellationToken);
            TempData["SheetImportSuccess"] = $"Exported {result.MemberCount} member(s) to the \"{result.Tab}\" tab.";
        }
        catch (GoogleOAuthRevokedException)
        {
            TempData["SheetImportError"] = "Google rejected the saved connection — reconnect the Google account.";
        }
        catch (Exception ex)
        {
            TempData["SheetImportError"] = $"Export failed: {ex.Message}";
        }
        return RedirectToAction(nameof(Export), new { linkshellId });
    }

    // Step 2 (Import page): read an uploaded .xlsx/.csv laid out like the template,
    // match its rows against the roster, and render a preview. The parsed rows are
    // round-tripped to the view as JSON so Commit needs no re-upload.
    [HttpPost("/linkshells/{linkshellId:int}/sheet/import-preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportPreview(int linkshellId, IFormFile? file, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var viewModel = await BuildViewModelAsync(linkshellId);
        if (viewModel is null) return NotFound();
        ApplyConnectedAtLocal(viewModel, user);

        if (file is null || file.Length == 0)
        {
            TempData["SheetImportError"] = "Choose an .xlsx (or .csv) file to import.";
            return View("Index", viewModel);
        }
        if (file.Length > 5 * 1024 * 1024)
        {
            TempData["SheetImportError"] = "That file is larger than 5 MB — export a smaller DKP sheet.";
            return View("Index", viewModel);
        }

        try
        {
            List<IList<object>> grid;
            using (var stream = file.OpenReadStream())
            {
                grid = XlsxImportReader.Read(stream, file.FileName);
            }

            var parsed = _template.ParseImport(grid);
            if (parsed.Count == 0)
            {
                TempData["SheetImportError"] =
                    "No member rows found. Use the same columns as the template (Member Name, Current DKP, Total DKP, Total DKP Spent).";
                return View("Index", viewModel);
            }

            viewModel.TemplatePreview = await _template.BuildPreviewAsync(linkshellId, parsed, file.FileName, cancellationToken);
            viewModel.ImportFileName = file.FileName;
            viewModel.ImportPayloadJson = JsonSerializer.Serialize(parsed);
        }
        catch (Exception ex)
        {
            TempData["SheetImportError"] = $"Could not read that file: {ex.Message}";
        }
        return View("Index", viewModel);
    }

    // Save import: commit the previewed rows (carried back as JSON) — sets each
    // matched member's Current DKP and seeds their lifetime totals.
    [HttpPost("/linkshells/{linkshellId:int}/sheet/import-commit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCommit(int linkshellId,
        [FromForm] string? payload, [FromForm] string? fileName, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        List<DkpImportRow>? parsed = null;
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try { parsed = JsonSerializer.Deserialize<List<DkpImportRow>>(payload); }
            catch (JsonException) { parsed = null; }
        }
        if (parsed is null || parsed.Count == 0)
        {
            TempData["SheetImportError"] = "Nothing to import — upload a file and preview it first.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        try
        {
            var label = string.IsNullOrWhiteSpace(fileName) ? "import" : fileName!;
            var result = await _template.CommitAsync(linkshellId, parsed, label, cancellationToken);
            var unmatched = result.Unmatched.Count == 0
                ? string.Empty
                : $" {result.Unmatched.Count} row(s) didn't match a member ({string.Join(", ", result.Unmatched.Take(5))}{(result.Unmatched.Count > 5 ? "…" : string.Empty)}).";
            TempData["SheetImportSuccess"] = $"Imported from \"{result.Tab}\": {result.Updated} member(s) updated.{unmatched}";
        }
        catch (Exception ex)
        {
            TempData["SheetImportError"] = $"Import failed: {ex.Message}";
        }
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // Downloads a small .xlsx with the canonical template layout + sample data so
    // a linkshell can see exactly how to format a tab they want to import. Static
    // content (no linkshell data), but gated like the rest of the page.
    [HttpGet("/linkshells/{linkshellId:int}/sheet/sample-template")]
    public async Task<IActionResult> DownloadSampleTemplate(int linkshellId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var bytes = SampleDkpTemplateWorkbook.Build();
        return File(bytes, SampleDkpTemplateWorkbook.ContentType, SampleDkpTemplateWorkbook.FileName);
    }

    private string BuildCallbackUri()
    {
        var path = string.IsNullOrWhiteSpace(_options.CallbackPath) ? "/signin-google-sheets" : _options.CallbackPath;
        return $"{Request.Scheme}://{Request.Host}{path}";
    }

    private async Task<bool> CanManageAsync(string appUserId, int linkshellId)
    {
        var membership = await _db.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(ul => ul.AppUserId == appUserId && ul.LinkshellId == linkshellId);
        return membership is not null && LinkshellRanks.IsLeaderOrOfficer(membership.Rank);
    }
}
