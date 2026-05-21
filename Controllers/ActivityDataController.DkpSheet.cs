using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Discord Activity read-only viewer for the linkshell's synced Google Sheet.
// Mirrors LinkshellAppDkpSheetController (the web viewer): reads tab titles +
// per-tab values via GoogleSheetsSyncService and shapes them for the client.
// Connecting/authorizing Google stays web-only (OAuth redirects don't work
// inside the Discord iframe). Member-gated; no officer requirement (read-only).
public sealed partial class ActivityDataController
{
    private const string DkpSheetDefaultReadColumns = "A:ZZ";

    // The Main tab carries an "(Updated)" sandbox area past column M that we
    // don't surface in the in-app render; pin Main to A:M (mirrors the web).
    private const string DkpSheetMainReadColumns = "A:M";
    private const string DkpSheetMainTabName = "Main";
    private const string DkpSheetTallyTabName = "Tally";

    public sealed record ActivityDkpSheetRowDto(int RowNumber, IReadOnlyList<string> Cells, string SearchText);

    public sealed record ActivityDkpSheetTabDto(
        string Name,
        bool IsMainTab,
        IReadOnlyList<string> Headers,
        IReadOnlyList<ActivityDkpSheetRowDto> Rows);

    public sealed record ActivityDkpSheetResponse(
        bool Connected,
        int LinkshellId,
        string? LinkshellName,
        string? OpenInSheetsUrl,
        string? Error,
        IReadOnlyList<ActivityDkpSheetTabDto> Tabs);

    [HttpGet("dkp-sheet")]
    public async Task<IActionResult> GetDkpSheetAsync([FromQuery] int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to view the DKP sheet." });
        if (linkshellId <= 0) return BadRequest(new { error = "A linkshell selection is required." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null) return Forbid();

        var linkshell = await _dbContext.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => new
            {
                l.LinkshellName,
                l.GoogleSpreadsheetId,
                l.GoogleOAuthRefreshTokenEnc
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (linkshell is null) return NotFound(new { error = "Linkshell not found." });

        Response.Headers.CacheControl = "no-store";

        var connected = !string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId)
                        && !string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc);
        var openInSheetsUrl = string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId)
            ? null
            : $"https://docs.google.com/spreadsheets/d/{linkshell.GoogleSpreadsheetId}/edit";

        if (!connected)
        {
            return Ok(new ActivityDkpSheetResponse(
                false,
                linkshellId,
                linkshell.LinkshellName,
                openInSheetsUrl,
                "Connect the Google Sheet on the web to view it here.",
                Array.Empty<ActivityDkpSheetTabDto>()));
        }

        try
        {
            var tabs = new List<ActivityDkpSheetTabDto>();
            var tabNames = await _sheets.GetSheetTitlesAsync(linkshellId, linkshell.GoogleSpreadsheetId!, cancellationToken);
            foreach (var tabName in tabNames)
            {
                var isMainTab = string.Equals(tabName, DkpSheetMainTabName, StringComparison.OrdinalIgnoreCase);
                var readColumns = isMainTab ? DkpSheetMainReadColumns : DkpSheetDefaultReadColumns;
                var rawRows = await _sheets.ReadAsync(
                    linkshellId,
                    linkshell.GoogleSpreadsheetId!,
                    $"{QuoteDkpSheetName(tabName)}!{readColumns}",
                    cancellationToken)
                    ?? new List<IList<object>>();

                tabs.Add(MapDkpSheetTab(tabName, isMainTab, rawRows));
            }

            return Ok(new ActivityDkpSheetResponse(
                true, linkshellId, linkshell.LinkshellName, openInSheetsUrl, null, tabs));
        }
        catch (Exception ex)
        {
            return Ok(new ActivityDkpSheetResponse(
                true,
                linkshellId,
                linkshell.LinkshellName,
                openInSheetsUrl,
                $"Failed to read sheet: {ex.Message}",
                Array.Empty<ActivityDkpSheetTabDto>()));
        }
    }

    private static ActivityDkpSheetTabDto MapDkpSheetTab(string tabName, bool isMainTab, IList<IList<object>> rawRows)
    {
        var columnCount = rawRows.Count == 0 ? 0 : rawRows.Max(row => row.Count);
        if (columnCount == 0)
        {
            return new ActivityDkpSheetTabDto(tabName, isMainTab, Array.Empty<string>(), Array.Empty<ActivityDkpSheetRowDto>());
        }

        var headerRow = rawRows.Count > 0 ? rawRows[0] : Array.Empty<object>();
        var headers = new List<string>(columnCount);
        for (var i = 0; i < columnCount; i++)
        {
            var header = i < headerRow.Count ? FormatDkpSheetCell(headerRow[i]) : string.Empty;
            headers.Add(string.IsNullOrWhiteSpace(header) ? DkpSheetColumnName(i + 1) : header);
        }

        var isTallyTab = string.Equals(tabName, DkpSheetTallyTabName, StringComparison.OrdinalIgnoreCase);
        var tallyMember = string.Empty;
        var rows = new List<ActivityDkpSheetRowDto>();
        for (var i = 1; i < rawRows.Count; i++)
        {
            var row = rawRows[i];
            var cells = new List<string>(columnCount);
            for (var c = 0; c < columnCount; c++)
            {
                cells.Add(c < row.Count ? FormatDkpSheetCell(row[c]) : string.Empty);
            }

            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            // Tally is laid out in per-member blocks: a row with the member name
            // in column A, then unnamed child rows (Kings Camp, Wyrms Camp, ...).
            // Carry the member name onto each child row's search text so filtering
            // by name reveals the whole block, not just the lone name row.
            // Mirrors Views/LinkshellAppDkpSheet/Index.cshtml.
            if (isTallyTab)
            {
                var firstCell = cells.Count > 0 ? cells[0].Trim() : string.Empty;
                if (!string.IsNullOrWhiteSpace(firstCell))
                {
                    tallyMember = firstCell;
                }
            }

            var search = string.Join(" ", cells).ToLowerInvariant();
            if (isTallyTab && !string.IsNullOrWhiteSpace(tallyMember))
            {
                search = (search + " " + tallyMember).ToLowerInvariant();
            }

            rows.Add(new ActivityDkpSheetRowDto(i + 1, cells, search));
        }

        return new ActivityDkpSheetTabDto(tabName, isMainTab, headers, rows);
    }

    private static string FormatDkpSheetCell(object? value) => value?.ToString() ?? string.Empty;

    private static string QuoteDkpSheetName(string tabName) => $"'{tabName.Replace("'", "''")}'";

    private static string DkpSheetColumnName(int columnNumber)
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
