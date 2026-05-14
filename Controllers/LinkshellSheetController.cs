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
    private readonly SheetMigrationService _migration;
    private readonly SheetSyncQueue _sheetSync;

    public LinkshellSheetController(
        ApplicationDbContext db,
        UserManager<AppUser> userManager,
        GoogleSheetsSyncService sheets,
        GoogleOAuthService oauth,
        IOptions<GoogleSheetsOptions> options,
        SheetMigrationService migration,
        SheetSyncQueue sheetSync)
    {
        _db = db;
        _userManager = userManager;
        _sheets = sheets;
        _oauth = oauth;
        _options = options.Value;
        _migration = migration;
        _sheetSync = sheetSync;
    }

    [HttpGet("/linkshells/{linkshellId:int}/sheet")]
    public async Task<IActionResult> Index(int linkshellId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var viewModel = await BuildViewModelAsync(linkshellId);
        if (viewModel is null) return NotFound();
        return View(viewModel);
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
            TabName = linkshell.GoogleSheetTabName,
            IsOAuthConfigured = _oauth.IsConfigured,
            IsOAuthConnected = !string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc),
            ConnectedGoogleEmail = linkshell.GoogleOAuthUserEmail,
            ConnectedAt = linkshell.GoogleOAuthConnectedAt,
            SheetSyncEnabled = linkshell.SheetSyncEnabled,
        };
    }

    [HttpPost("/linkshells/{linkshellId:int}/sheet/sync-toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleSync(int linkshellId, [FromForm] bool enabled, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);
        if (linkshell is null) return NotFound();

        linkshell.SheetSyncEnabled = enabled;
        await _db.SaveChangesAsync(cancellationToken);
        TempData["SheetConfigSuccess"] = enabled
            ? "Live sync to the sheet is now ENABLED. DKP changes will update column C of matching rows."
            : "Live sync to the sheet is now DISABLED. The sheet won't be touched.";
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
            var linkshellId = await _oauth.HandleCallbackAsync(code, state, user.Id, BuildCallbackUri(), cancellationToken);
            if (!await CanManageAsync(user.Id, linkshellId))
            {
                return Forbid();
            }
            await _sheetSync.EnqueueAsync(linkshellId, cancellationToken);
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

    [HttpPost("/linkshells/{linkshellId:int}/sheet/config")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveConfig(int linkshellId, [FromForm] string? spreadsheetId, [FromForm] string? tabName)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId);
        if (linkshell is null) return NotFound();

        linkshell.GoogleSpreadsheetId = string.IsNullOrWhiteSpace(spreadsheetId) ? null : spreadsheetId.Trim();
        linkshell.GoogleSheetTabName = string.IsNullOrWhiteSpace(tabName) ? null : tabName.Trim();

        await _db.SaveChangesAsync();
        TempData["SheetConfigSuccess"] = "Google Sheet configuration saved.";
        if (!string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId) && !string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            await _sheetSync.EnqueueAsync(linkshellId);
        }
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/sheet/import-preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportPreview(int linkshellId, [FromForm] string? readRange, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var viewModel = await BuildViewModelAsync(linkshellId);
        if (viewModel is null) return NotFound();

        if (string.IsNullOrWhiteSpace(viewModel.SpreadsheetId))
        {
            TempData["SheetImportError"] = "Configure a spreadsheet ID before previewing an import.";
            return View("Index", viewModel);
        }
        if (!viewModel.IsOAuthConnected)
        {
            TempData["SheetImportError"] = "Connect a Google account before previewing an import.";
            return View("Index", viewModel);
        }

        try
        {
            var preview = await _migration.BuildPreviewAsync(linkshellId, viewModel.SpreadsheetId!, readRange, cancellationToken);
            viewModel.Preview = preview;
            viewModel.PreviewRange = preview.Range;
        }
        catch (Exception ex)
        {
            TempData["SheetImportError"] = $"Preview failed: {ex.Message}";
        }
        return View("Index", viewModel);
    }

    [HttpPost("/linkshells/{linkshellId:int}/sheet/import-commit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCommit(int linkshellId, [FromForm] string? readRange, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var linkshell = await _db.Linkshells.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);
        if (linkshell?.GoogleSpreadsheetId is null)
        {
            TempData["SheetImportError"] = "Configure a spreadsheet ID before importing.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }
        if (string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            TempData["SheetImportError"] = "Connect a Google account before importing.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        try
        {
            var result = await _migration.CommitAsync(linkshellId, linkshell.GoogleSpreadsheetId, readRange, cancellationToken);
            TempData["SheetImportSuccess"] = $"Imported: {result.Updated} updated, {result.Created} new placeholder member(s), {result.Unchanged} unchanged.";
            await _sheetSync.EnqueueAsync(linkshellId, cancellationToken);
        }
        catch (Exception ex)
        {
            TempData["SheetImportError"] = $"Import failed: {ex.Message}";
        }
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/sheet/resync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resync(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        await _sheetSync.EnqueueAsync(linkshellId, cancellationToken);
        TempData["SheetConfigSuccess"] = "Sync queued. The sheet should update within a few seconds.";
        return RedirectToAction(nameof(Index), new { linkshellId });
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
        return membership is not null
               && !string.IsNullOrWhiteSpace(membership.Rank)
               && (membership.Rank.Equals("Leader", StringComparison.OrdinalIgnoreCase)
                   || membership.Rank.Equals("Officer", StringComparison.OrdinalIgnoreCase));
    }
}
