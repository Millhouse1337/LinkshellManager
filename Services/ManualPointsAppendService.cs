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

        await WriteColumnAsync(
            linkshellId: entry.LinkshellId,
            header: BuildColumnHeader(entry),
            monthYear: entry.OccurredAt.ToString("MMM yyyy", CultureInfo.InvariantCulture),
            entries: new[] { entry },
            cancellationToken: cancellationToken);
    }

    // Writes one ManualPoints column for every winning bid in an auction
    // close. Fired by the SheetSyncQueue after AuctionController.CloseAuction
    // commits its AuctionHistory + DkpLedgerEntries. Idempotent: only
    // unstamped AuctionSpent entries linked to this AuctionHistory are written;
    // if every entry already has SheetAppendedAt, this is a no-op.
    public async Task AppendAuctionDeductionsAsync(int auctionHistoryId, CancellationToken cancellationToken)
    {
        var auctionHistory = await _db.AuctionHistories
            .FirstOrDefaultAsync(history => history.Id == auctionHistoryId, cancellationToken);
        if (auctionHistory is null)
        {
            _logger.LogDebug("ManualPoints skip: auction history {Id} not found.", auctionHistoryId);
            return;
        }

        var pending = await _db.DkpLedgerEntries
            .Where(entry => entry.SourceAuctionHistoryId == auctionHistoryId
                            && entry.EntryType == "AuctionSpent"
                            && !entry.SheetAppendedAt.HasValue
                            && entry.CharacterName != null)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            _logger.LogDebug("ManualPoints skip: auction history {Id} has no pending deductions.", auctionHistoryId);
            return;
        }

        var referenceDate = pending.Min(entry => entry.OccurredAt);
        var datePart = referenceDate.ToString("M/d", CultureInfo.InvariantCulture);
        var titlePart = (auctionHistory.AuctionTitle ?? "Auction").Trim();
        if (titlePart.Length > 30) titlePart = titlePart.Substring(0, 30);
        var header = string.IsNullOrEmpty(titlePart) ? $"{datePart} Auction" : $"{datePart} {titlePart}";
        var monthYear = referenceDate.ToString("MMM yyyy", CultureInfo.InvariantCulture);

        await WriteColumnAsync(
            linkshellId: auctionHistory.LinkshellId,
            header: header,
            monthYear: monthYear,
            entries: pending,
            cancellationToken: cancellationToken);
    }

    // Same shape as AppendAuctionDeductionsAsync but for event-close loot
    // payouts. EventHistory is already the canonical parent for these ledger
    // entries (set inline at EventController.Lifecycle.EndEventCoreAsync time),
    // so no extra FK plumbing is needed here.
    public async Task AppendEventLootDeductionsAsync(int eventHistoryId, CancellationToken cancellationToken)
    {
        var eventHistory = await _db.EventHistories
            .FirstOrDefaultAsync(history => history.Id == eventHistoryId, cancellationToken);
        if (eventHistory is null)
        {
            _logger.LogDebug("ManualPoints skip: event history {Id} not found.", eventHistoryId);
            return;
        }

        var pending = await _db.DkpLedgerEntries
            .Where(entry => entry.EventHistoryId == eventHistoryId
                            && entry.EntryType == "LootSpent"
                            && !entry.SheetAppendedAt.HasValue
                            && entry.CharacterName != null)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            _logger.LogDebug("ManualPoints skip: event history {Id} has no pending loot deductions.", eventHistoryId);
            return;
        }

        var referenceDate = pending.Min(entry => entry.OccurredAt);
        var datePart = referenceDate.ToString("M/d", CultureInfo.InvariantCulture);
        var titlePart = (eventHistory.EventName ?? "Loot").Trim();
        if (titlePart.Length > 30) titlePart = titlePart.Substring(0, 30);
        var header = string.IsNullOrEmpty(titlePart) ? $"{datePart} Loot" : $"{datePart} {titlePart}";
        var monthYear = referenceDate.ToString("MMM yyyy", CultureInfo.InvariantCulture);

        await WriteColumnAsync(
            linkshellId: eventHistory.LinkshellId,
            header: header,
            monthYear: monthYear,
            entries: pending,
            cancellationToken: cancellationToken);
    }

    // Shared write path: claims a brand-new column on the ManualPoints tab,
    // writes the header block (rows 1-4), looks up or appends each member's
    // row in column A, and writes their amount. Stamps SheetAppendedAt on
    // every ledger entry so retries skip them.
    private async Task WriteColumnAsync(
        int linkshellId,
        string header,
        string monthYear,
        IReadOnlyCollection<DkpLedgerEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0) return;

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            _logger.LogDebug("ManualPoints skip: linkshell {Id} not found.", linkshellId);
            return;
        }
        if (!linkshell.SheetSyncEnabled
            || string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId)
            || string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            _logger.LogDebug("ManualPoints skip: linkshell {Id} not configured for sync.", linkshellId);
            return;
        }

        var tab = string.IsNullOrWhiteSpace(linkshell.ManualPointsTabName) ? DefaultTabName : linkshell.ManualPointsTabName!;
        var spreadsheetId = linkshell.GoogleSpreadsheetId!;

        // Pick a fresh column on the right edge of the existing header row.
        var headerRange = $"{tab}!1:1";
        var headerRows = await _sheets.ReadAsync(linkshell.Id, spreadsheetId, headerRange, cancellationToken);
        var headerRow = headerRows is { Count: > 0 } ? headerRows[0] : new List<object>();
        var newColumnIndex = FindFirstEmptyColumn(headerRow, FirstEventColumn);
        var newColumnLetter = ColumnIndexToLetter(newColumnIndex);

        // Header block (rows 1-4). Single write so a transient failure
        // doesn't leave a half-formed header behind for the next retry.
        var totalFormula = $"=SUM({newColumnLetter}{MemberStartRow}:{newColumnLetter}{TotalFormulaEndRow})";
        await _sheets.WriteAsync(
            linkshell.Id,
            spreadsheetId,
            $"{tab}!{newColumnLetter}1:{newColumnLetter}4",
            new List<IList<object>>
            {
                new List<object> { header },
                new List<object> { string.Empty },
                new List<object> { monthYear },
                new List<object> { totalFormula },
            },
            cancellationToken);

        // Walk the existing column-A name list once. New characters get
        // appended at the bottom and the in-memory map is updated so the
        // batch's subsequent rows can resolve their row index without
        // re-reading.
        var memberColRange = $"{tab}!A{MemberStartRow}:A";
        var memberRows = await _sheets.ReadAsync(linkshell.Id, spreadsheetId, memberColRange, cancellationToken) ?? new List<IList<object>>();
        var nameToRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < memberRows.Count; i++)
        {
            var row = memberRows[i];
            if (row.Count == 0) continue;
            var name = row[0]?.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            nameToRow.TryAdd(name!, MemberStartRow + i);
        }
        var nextAppendRow = MemberStartRow + memberRows.Count;

        // Merge per-character so two ledger entries for the same character
        // collapse into a single cell (rare but possible if a winner takes
        // multiple items in one auction).
        var amountsByCharacter = entries
            .GroupBy(e => e.CharacterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Amount), StringComparer.OrdinalIgnoreCase);

        foreach (var pair in amountsByCharacter)
        {
            if (!nameToRow.TryGetValue(pair.Key, out var row))
            {
                row = nextAppendRow++;
                await _sheets.WriteAsync(
                    linkshell.Id,
                    spreadsheetId,
                    $"{tab}!A{row}",
                    new List<IList<object>> { new List<object> { pair.Key } },
                    cancellationToken);
                nameToRow[pair.Key] = row;
                _logger.LogInformation("ManualPoints added new member row: {Character} at row {Row}.", pair.Key, row);
            }

            await _sheets.WriteAsync(
                linkshell.Id,
                spreadsheetId,
                $"{tab}!{newColumnLetter}{row}",
                new List<IList<object>> { new List<object> { pair.Value } },
                cancellationToken);
        }

        var stampUtc = DateTime.UtcNow;
        foreach (var entry in entries)
        {
            entry.SheetAppendedAt = stampUtc;
        }
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "ManualPoints append: {Header} -> {Tab}!{Col} ({Count} entries across {Members} member rows).",
            header, tab, newColumnLetter, entries.Count, amountsByCharacter.Count);
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
