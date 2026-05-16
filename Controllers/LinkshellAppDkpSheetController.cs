using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public sealed class LinkshellAppDkpSheetController : Controller
{
    private const string DefaultReadColumns = "A:ZZ";

    // The Main tab carries an "(Updated)" debug/sandbox area to the right of
    // column M that we don't want surfaced in the in-app render. Pin Main to
    // A:M so the view only shows the canonical totals + camp columns.
    private const string MainTabReadColumns = "A:M";
    private const string MainTabName = "Main";

    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly GoogleSheetsSyncService _sheets;

    public LinkshellAppDkpSheetController(
        ApplicationDbContext db,
        UserManager<AppUser> userManager,
        GoogleSheetsSyncService sheets)
    {
        _db = db;
        _userManager = userManager;
        _sheets = sheets;
    }

    [HttpGet("/linkshells/{linkshellId:int}/app-dkp-sheet")]
    public async Task<IActionResult> Index(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var isMember = await _db.AppUserLinkshells
            .AsNoTracking()
            .AnyAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId, cancellationToken);
        if (!isMember) return Forbid();

        var linkshell = await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => new
            {
                l.LinkshellName,
                l.GoogleSpreadsheetId,
                l.GoogleOAuthRefreshTokenEnc
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (linkshell is null) return NotFound();

        var viewModel = new LinkshellAppDkpSheetViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            IsConfigured = !string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId)
                           && !string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc),
        };

        if (!string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId))
        {
            viewModel.OpenInSheetsUrl = $"https://docs.google.com/spreadsheets/d/{linkshell.GoogleSpreadsheetId}/edit";
        }

        if (!viewModel.IsConfigured)
        {
            viewModel.ErrorMessage = "Configure the Google Sheet and connect a Google account first.";
            return View(viewModel);
        }

        try
        {
            var tabNames = await _sheets.GetSheetTitlesAsync(linkshellId, linkshell.GoogleSpreadsheetId!, cancellationToken);
            foreach (var tabName in tabNames)
            {
                var readColumns = string.Equals(tabName, MainTabName, StringComparison.OrdinalIgnoreCase)
                    ? MainTabReadColumns
                    : DefaultReadColumns;
                var rawRows = await _sheets.ReadAsync(
                    linkshellId,
                    linkshell.GoogleSpreadsheetId!,
                    $"{QuoteSheetName(tabName)}!{readColumns}",
                    cancellationToken)
                    ?? new List<IList<object>>();

                viewModel.Tabs.Add(MapTab(tabName, rawRows));
            }
        }
        catch (Exception ex)
        {
            viewModel.ErrorMessage = $"Failed to read sheet: {ex.Message}";
        }

        return View(viewModel);
    }

    private static AppDkpSheetTabViewModel MapTab(string tabName, IList<IList<object>> rawRows)
    {
        var columnCount = rawRows.Count == 0 ? 0 : rawRows.Max(row => row.Count);
        var tab = new AppDkpSheetTabViewModel
        {
            Name = tabName,
            ColumnCount = columnCount,
        };

        if (columnCount == 0)
        {
            return tab;
        }

        var headerRow = rawRows.Count > 0 ? rawRows[0] : Array.Empty<object>();
        for (var i = 0; i < columnCount; i++)
        {
            var header = i < headerRow.Count ? FormatCell(headerRow[i]) : string.Empty;
            tab.Headers.Add(string.IsNullOrWhiteSpace(header) ? ColumnName(i + 1) : header);
        }

        for (var i = 1; i < rawRows.Count; i++)
        {
            var row = rawRows[i];
            var cells = new List<string>(columnCount);
            for (var c = 0; c < columnCount; c++)
            {
                cells.Add(c < row.Count ? FormatCell(row[c]) : string.Empty);
            }

            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            tab.Rows.Add(new AppDkpSheetRowViewModel
            {
                RowNumber = i + 1,
                Cells = cells,
            });
        }

        return tab;
    }

    private static string FormatCell(object? value)
        => value?.ToString() ?? string.Empty;

    private static string QuoteSheetName(string tabName)
        => $"'{tabName.Replace("'", "''")}'";

    private static string ColumnName(int columnNumber)
    {
        var n = columnNumber;
        var name = string.Empty;
        while (n > 0)
        {
            n--;
            name = (char)('A' + (n % 26)) + name;
            n /= 26;
        }
        return name;
    }
}
