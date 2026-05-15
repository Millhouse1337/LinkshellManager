using System.Globalization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Writes a DKP audit adjustment to the user's Google Sheet "ManualPoints" tab.
// The tab is laid out as a matrix:
//   Col A   = Member name
//   Col C   = Per-row total (formula across all event columns)
//   Col D+  = One column per event/audit:
//             Row 1 = header  ("5/14 Audit: test")
//             Row 2 = subheader ("Bids" in the existing pattern)
//             Row 3 = month/year ("May 2026")
//             Row 4 = column total =SUM(<col>5:<col>200)
//             Row 5+ = per-member value in that event's column
//
// Each audit adds a brand new column on the right. Idempotent via
// DkpLedgerEntry.SheetAppendedAt; runs no-op if the stamp is already set.
public sealed class ManualPointsAppendService
{
    private const string DefaultTabName = "ManualPoints";
    private const int MemberStartRow = 5;          // first member row in column A
    private const int FirstEventColumn = 4;        // column D — earlier columns are A/B/C metadata
    private const int TotalFormulaEndRow = 200;    // safe upper bound for the SUM formula

    private readonly ApplicationDbContext _db;
    private readonly GoogleSheetsSyncService _sheets;
    private readonly ILogger<ManualPointsAppendService> _logger;

    public ManualPointsAppendService(
        ApplicationDbContext db,
        GoogleSheetsSyncService sheets,
        ILogger<ManualPointsAppendService> logger)
    {
        _db = db;
        _sheets = sheets;
        _logger = logger;
    }

    public async Task AppendAuditAsync(int dkpLedgerEntryId, CancellationToken cancellationToken)
    {
        var entry = await _db.DkpLedgerEntries
            .FirstOrDefaultAsync(e => e.Id == dkpLedgerEntryId, cancellationToken);
        if (entry is null)
        {
            _logger.LogDebug("ManualPoints skip: ledger entry {Id} not found.", dkpLedgerEntryId);
            return;
        }
        if (entry.SheetAppendedAt.HasValue)
        {
            _logger.LogDebug("ManualPoints skip: ledger entry {Id} already appended.", dkpLedgerEntryId);
            return;
        }
        if (string.IsNullOrWhiteSpace(entry.CharacterName))
        {
            _logger.LogDebug("ManualPoints skip: ledger entry {Id} has no character name.", dkpLedgerEntryId);
            return;
        }

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == entry.LinkshellId, cancellationToken);
        if (linkshell is null)
        {
            _logger.LogDebug("ManualPoints skip: linkshell {Id} not found.", entry.LinkshellId);
            return;
        }
        if (!linkshell.SheetSyncEnabled
            || string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId)
            || string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            _logger.LogDebug("ManualPoints skip: linkshell {Id} not configured for sync.", entry.LinkshellId);
            return;
        }

        var tab = string.IsNullOrWhiteSpace(linkshell.ManualPointsTabName) ? DefaultTabName : linkshell.ManualPointsTabName!;
        var spreadsheetId = linkshell.GoogleSpreadsheetId!;

        // 1. Find first empty header column in row 1 (search columns D..ZZ).
        var headerRange = $"{tab}!1:1";
        var headerRows = await _sheets.ReadAsync(linkshell.Id, spreadsheetId, headerRange, cancellationToken);
        var headerRow = headerRows is { Count: > 0 } ? headerRows[0] : new List<object>();
        var newColumnIndex = FindFirstEmptyColumn(headerRow, FirstEventColumn);
        var newColumnLetter = ColumnIndexToLetter(newColumnIndex);

        // 2. Find the member's row by scanning column A.
        var memberColRange = $"{tab}!A{MemberStartRow}:A";
        var memberRows = await _sheets.ReadAsync(linkshell.Id, spreadsheetId, memberColRange, cancellationToken);
        var memberRowIndex = FindMemberRow(memberRows, entry.CharacterName!);
        if (memberRowIndex == 0)
        {
            // Append the member name to the next available row in column A.
            memberRowIndex = MemberStartRow + (memberRows?.Count ?? 0);
            await _sheets.WriteAsync(
                linkshell.Id,
                spreadsheetId,
                $"{tab}!A{memberRowIndex}",
                new List<IList<object>> { new List<object> { entry.CharacterName! } },
                cancellationToken);
            _logger.LogInformation("ManualPoints added new member row: {Character} at row {Row}.", entry.CharacterName, memberRowIndex);
        }

        // 3. Write column header (rows 1-4) plus the member's value on row N.
        var header = BuildColumnHeader(entry);
        var monthYear = entry.OccurredAt.ToString("MMM yyyy", CultureInfo.InvariantCulture);
        var totalFormula = $"=SUM({newColumnLetter}{MemberStartRow}:{newColumnLetter}{TotalFormulaEndRow})";

        // Header block (rows 1-4) in one shot.
        await _sheets.WriteAsync(
            linkshell.Id,
            spreadsheetId,
            $"{tab}!{newColumnLetter}1:{newColumnLetter}4",
            new List<IList<object>>
            {
                new List<object> { header },
                new List<object> { string.Empty },     // subheader stays blank for audits to distinguish from real bid columns
                new List<object> { monthYear },
                new List<object> { totalFormula },
            },
            cancellationToken);

        // Member's value cell.
        await _sheets.WriteAsync(
            linkshell.Id,
            spreadsheetId,
            $"{tab}!{newColumnLetter}{memberRowIndex}",
            new List<IList<object>> { new List<object> { entry.Amount } },
            cancellationToken);

        entry.SheetAppendedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "ManualPoints append: ledger {Id} -> {Tab}!{Col} (member row {Row}, amount {Amount}).",
            dkpLedgerEntryId, tab, newColumnLetter, memberRowIndex, entry.Amount);
    }

    private static int FindFirstEmptyColumn(IList<object> headerRow, int startIndex)
    {
        for (var i = startIndex; i <= headerRow.Count; i++)
        {
            var cell = i <= headerRow.Count ? headerRow[i - 1] : null;
            if (cell is null || string.IsNullOrWhiteSpace(cell.ToString()))
            {
                return i;
            }
        }
        return headerRow.Count + 1;
    }

    private static int FindMemberRow(IList<IList<object>>? memberRows, string characterName)
    {
        if (memberRows is null) return 0;
        for (var i = 0; i < memberRows.Count; i++)
        {
            var row = memberRows[i];
            if (row.Count == 0) continue;
            var name = row[0]?.ToString();
            if (string.Equals(name, characterName, StringComparison.OrdinalIgnoreCase))
            {
                return MemberStartRow + i;
            }
        }
        return 0;
    }

    private static string BuildColumnHeader(DkpLedgerEntry entry)
    {
        var datePart = entry.OccurredAt.ToString("M/d", CultureInfo.InvariantCulture);
        var reason = (entry.Details ?? string.Empty).Trim();
        if (reason.Length > 30) reason = reason.Substring(0, 30);
        return string.IsNullOrEmpty(reason) ? $"{datePart} Audit" : $"{datePart} Audit: {reason}";
    }

    private static string ColumnIndexToLetter(int columnIndex)
    {
        var n = columnIndex;
        var letters = string.Empty;
        while (n > 0)
        {
            n--;
            letters = (char)('A' + (n % 26)) + letters;
            n /= 26;
        }
        return letters;
    }
}
