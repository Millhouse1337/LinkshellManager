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
//             Row 2 = subheader (ColumnSubheader, "Bids" -- the Main-tab
//                     rollup keys off this; must be set, never blank)
//             Row 3 = month/year ("May 2026")
//             Row 4 = column total =SUM(<col>5:<col>200)
//             Row 5+ = per-member value in that event's column
//
// Each audit adds a brand new column on the right. Idempotent via
// DkpLedgerEntry.SheetAppendedAt; runs no-op if the stamp is already set.
public sealed class ManualPointsAppendService
{
    private const string DefaultTabName = "ManualPoints";
    // Row-2 category tag every ManualPoints column carries. The user's
    // template Main-tab rollup keys off this subheader (it's the
    // data-validation value shared by all columns), so app-created columns
    // MUST set it -- an empty row 2 silently excludes the column from Main.
    private const string ColumnSubheader = "Bids";
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
        // ManualPoints is deductions-only. Positive (credit) adjustments go
        // to the AttInput tab instead (AttInputAppendService.AppendMiscDkpAsync).
        // Guard runs before the SheetAppendedAt check so a credit never
        // claims the shared idempotency stamp.
        if (entry.Amount >= 0)
        {
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

    // ToD loot lands on the ManualPoints tab grouped one column per
    // (linkshell, calendar day). Unlike auction/event closes, ToD loot
    // trickles in one item per addon call and can be edited/deleted later,
    // so this RECOMPUTES the whole day's column from the current
    // TodLootDetail rows (the source of truth) every time something changes.
    // That makes it idempotent and self-correcting: an edit/refund/delete
    // just changes the loot rows, and the next recompute rewrites the day's
    // column (including clearing characters that no longer owe anything).
    //
    // The day column is matched by an exact row-1 header ("yyyy-MM-dd ToD
    // Loot") so we only ever touch columns this path created -- auction /
    // event / audit columns use different headers and are never clobbered.
    public async Task AppendTodLootDayAsync(int todId, CancellationToken cancellationToken)
    {
        var tod = await _db.Tods
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == todId, cancellationToken);
        if (tod is null)
        {
            _logger.LogDebug("ManualPoints skip: tod {Id} not found.", todId);
            return;
        }

        var day = (tod.Time ?? tod.TimeStamp)?.Date;
        if (day is null)
        {
            _logger.LogDebug("ManualPoints skip: tod {Id} has no date.", todId);
            return;
        }
        var dayStart = DateTime.SpecifyKind(day.Value, DateTimeKind.Utc);
        var dayEnd = dayStart.AddDays(1);

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == tod.LinkshellId, cancellationToken);
        if (linkshell is null)
        {
            _logger.LogDebug("ManualPoints skip: linkshell {Id} not found.", tod.LinkshellId);
            return;
        }
        if (!linkshell.SheetSyncEnabled
            || string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId)
            || string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            _logger.LogDebug("ManualPoints skip: linkshell {Id} not configured for sync.", tod.LinkshellId);
            return;
        }

        var tab = string.IsNullOrWhiteSpace(linkshell.ManualPointsTabName) ? DefaultTabName : linkshell.ManualPointsTabName!;
        var spreadsheetId = linkshell.GoogleSpreadsheetId!;

        // Recompute the day's per-character net deduction from the loot rows.
        // ActualDeductedDkp is the real amount removed (Hybrid-aware); fall
        // back to WinningDkpSpent. LootCouncil rows carry no DKP so they net
        // to zero and drop out below.
        var details = await _db.TodLootDetails
            .AsNoTracking()
            .Where(d => d.Tod != null
                && d.Tod.LinkshellId == tod.LinkshellId
                && d.ItemWinner != null
                && (d.Tod.Time ?? d.Tod.TimeStamp) >= dayStart
                && (d.Tod.Time ?? d.Tod.TimeStamp) < dayEnd)
            .Select(d => new { d.ItemWinner, d.ActualDeductedDkp, d.WinningDkpSpent })
            .ToListAsync(cancellationToken);

        var totalsByCharacter = details
            .Select(d => new
            {
                Name = (d.ItemWinner ?? string.Empty).Trim(),
                Amount = d.ActualDeductedDkp ?? (double?)d.WinningDkpSpent ?? 0d
            })
            .Where(x => x.Name.Length > 0 && x.Amount > 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => -g.Sum(x => x.Amount), StringComparer.OrdinalIgnoreCase);

        var header = $"{dayStart:yyyy-MM-dd} ToD Loot";
        var monthYear = dayStart.ToString("MMM yyyy", CultureInfo.InvariantCulture);

        // Find this day's existing column by exact header, else claim a new one.
        var headerRows = await _sheets.ReadAsync(linkshell.Id, spreadsheetId, $"{tab}!1:1", cancellationToken);
        var headerRow = headerRows is { Count: > 0 } ? headerRows[0] : new List<object>();
        var columnIndex = -1;
        for (var i = FirstEventColumn; i <= headerRow.Count; i++)
        {
            var cell = headerRow[i - 1]?.ToString();
            if (!string.IsNullOrWhiteSpace(cell)
                && string.Equals(cell.Trim(), header, StringComparison.OrdinalIgnoreCase))
            {
                columnIndex = i;
                break;
            }
        }
        var isNewColumn = columnIndex < 0;
        if (isNewColumn)
        {
            columnIndex = FindFirstEmptyColumn(headerRow, FirstEventColumn);
        }
        var colLetter = ColumnIndexToLetter(columnIndex);

        if (isNewColumn)
        {
            var totalFormula = $"=SUM({colLetter}{MemberStartRow}:{colLetter}{TotalFormulaEndRow})";
            await _sheets.WriteAsync(
                linkshell.Id,
                spreadsheetId,
                $"{tab}!{colLetter}1:{colLetter}4",
                new List<IList<object>>
                {
                    new List<object> { header },
                    new List<object> { ColumnSubheader },
                    new List<object> { monthYear },
                    new List<object> { totalFormula },
                },
                cancellationToken);
        }
        else
        {
            // Self-heal columns created before the subheader fix: a recompute
            // (loot add/edit/delete on this day) rewrites row 2 so older
            // ToD-loot columns start counting toward the Main-tab rollup.
            await _sheets.WriteAsync(
                linkshell.Id,
                spreadsheetId,
                $"{tab}!{colLetter}2",
                new List<IList<object>> { new List<object> { ColumnSubheader } },
                cancellationToken);
        }

        var memberRows = await _sheets.ReadAsync(linkshell.Id, spreadsheetId, $"{tab}!A{MemberStartRow}:A", cancellationToken)
            ?? new List<IList<object>>();
        var nameToRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < memberRows.Count; i++)
        {
            var name = memberRows[i].Count > 0 ? memberRows[i][0]?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(name)) nameToRow.TryAdd(name!, MemberStartRow + i);
        }
        var nextAppendRow = MemberStartRow + memberRows.Count;

        // Existing values in this column so a recompute can BLANK characters
        // who no longer owe (refunded / deleted / edited to someone else).
        IList<IList<object>> existingCol = new List<IList<object>>();
        if (!isNewColumn && memberRows.Count > 0)
        {
            existingCol = await _sheets.ReadAsync(
                linkshell.Id,
                spreadsheetId,
                $"{tab}!{colLetter}{MemberStartRow}:{colLetter}{MemberStartRow + memberRows.Count - 1}",
                cancellationToken) ?? new List<IList<object>>();
        }

        foreach (var pair in totalsByCharacter)
        {
            if (!nameToRow.TryGetValue(pair.Key, out var row))
            {
                row = nextAppendRow++;
                await _sheets.WriteAsync(
                    linkshell.Id, spreadsheetId, $"{tab}!A{row}",
                    new List<IList<object>> { new List<object> { pair.Key } },
                    cancellationToken);
                nameToRow[pair.Key] = row;
            }
            await _sheets.WriteAsync(
                linkshell.Id, spreadsheetId, $"{tab}!{colLetter}{row}",
                new List<IList<object>> { new List<object> { pair.Value } },
                cancellationToken);
        }

        for (var i = 0; i < existingCol.Count; i++)
        {
            var name = memberRows.Count > i && memberRows[i].Count > 0
                ? memberRows[i][0]?.ToString()
                : null;
            var hadValue = existingCol[i].Count > 0
                && !string.IsNullOrWhiteSpace(existingCol[i][0]?.ToString());
            if (!string.IsNullOrWhiteSpace(name)
                && hadValue
                && !totalsByCharacter.ContainsKey(name!.Trim()))
            {
                await _sheets.WriteAsync(
                    linkshell.Id, spreadsheetId, $"{tab}!{colLetter}{MemberStartRow + i}",
                    new List<IList<object>> { new List<object> { string.Empty } },
                    cancellationToken);
            }
        }

        _logger.LogInformation(
            "ManualPoints ToD-loot recompute: {Header} -> {Tab}!{Col} ({Members} member rows, newColumn={New}).",
            header, tab, colLetter, totalsByCharacter.Count, isNewColumn);
    }

    // Event analogue of AppendTodLootDayAsync. AppendEventLootDeductionsAsync
    // writes a CLOSED event's loot as a single ManualPoints column once at
    // close (append-once, SheetAppendedAt-gated) — so a later Loot History
    // edit/refund/delete of event loot would otherwise leave that column
    // frozen. This RECOMPUTES the event's column from the current
    // EventLootDetail rows (the post-close source of truth: they're preserved
    // and re-parented to EventHistoryId at close), making it idempotent and
    // self-correcting exactly like the ToD path — including blanking
    // characters who no longer owe.
    //
    // The column is matched by the SAME row-1 header the close-time append
    // wrote. Close used referenceDate = min(LootSpent.OccurredAt); every
    // LootSpent entry's OccurredAt is set to the event end time, which is
    // EventHistory.EndTime — so the header is reproduced deterministically
    // from the EventHistory alone. Only event/loot columns built by this
    // header are ever touched; ToD/auction/audit columns use different
    // headers and are never clobbered.
    public async Task RecomputeEventLootColumnAsync(int eventHistoryId, CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == eventHistoryId, cancellationToken);
        if (history is null)
        {
            _logger.LogDebug("ManualPoints skip: event history {Id} not found.", eventHistoryId);
            return;
        }
        if (history.EndTime is null)
        {
            _logger.LogDebug("ManualPoints skip: event history {Id} has no end time.", eventHistoryId);
            return;
        }

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == history.LinkshellId, cancellationToken);
        if (linkshell is null)
        {
            _logger.LogDebug("ManualPoints skip: linkshell {Id} not found.", history.LinkshellId);
            return;
        }
        if (!linkshell.SheetSyncEnabled
            || string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId)
            || string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            _logger.LogDebug("ManualPoints skip: linkshell {Id} not configured for sync.", history.LinkshellId);
            return;
        }

        var tab = string.IsNullOrWhiteSpace(linkshell.ManualPointsTabName) ? DefaultTabName : linkshell.ManualPointsTabName!;
        var spreadsheetId = linkshell.GoogleSpreadsheetId!;

        // Header derived exactly as AppendEventLootDeductionsAsync did at
        // close (referenceDate == event end == EventHistory.EndTime).
        var referenceDate = history.EndTime.Value;
        var datePart = referenceDate.ToString("M/d", CultureInfo.InvariantCulture);
        var titlePart = (history.EventName ?? "Loot").Trim();
        if (titlePart.Length > 30) titlePart = titlePart.Substring(0, 30);
        var header = string.IsNullOrEmpty(titlePart) ? $"{datePart} Loot" : $"{datePart} {titlePart}";
        var monthYear = referenceDate.ToString("MMM yyyy", CultureInfo.InvariantCulture);

        // Recompute the per-character net deduction from the surviving loot
        // rows. ActualDeductedDkp is the real amount removed; fall back to
        // WinningDkpSpent. LootCouncil rows carry no DKP so they net to zero
        // and drop out below.
        var details = await _db.EventLootDetails
            .AsNoTracking()
            .Where(d => d.EventHistoryId == eventHistoryId && d.ItemWinner != null)
            .Select(d => new { d.ItemWinner, d.ActualDeductedDkp, d.WinningDkpSpent })
            .ToListAsync(cancellationToken);

        var totalsByCharacter = details
            .Select(d => new
            {
                Name = (d.ItemWinner ?? string.Empty).Trim(),
                Amount = d.ActualDeductedDkp ?? (double?)d.WinningDkpSpent ?? 0d
            })
            .Where(x => x.Name.Length > 0 && x.Amount > 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => -g.Sum(x => x.Amount), StringComparer.OrdinalIgnoreCase);

        var headerRows = await _sheets.ReadAsync(linkshell.Id, spreadsheetId, $"{tab}!1:1", cancellationToken);
        var headerRow = headerRows is { Count: > 0 } ? headerRows[0] : new List<object>();
        var columnIndex = -1;
        for (var i = FirstEventColumn; i <= headerRow.Count; i++)
        {
            var cell = headerRow[i - 1]?.ToString();
            if (!string.IsNullOrWhiteSpace(cell)
                && string.Equals(cell.Trim(), header, StringComparison.OrdinalIgnoreCase))
            {
                columnIndex = i;
                break;
            }
        }
        var isNewColumn = columnIndex < 0;
        if (isNewColumn && totalsByCharacter.Count == 0)
        {
            // Nothing to write and no column to clear (e.g. every loot row was
            // deleted, or sync was off at close). Don't create an empty column.
            _logger.LogDebug(
                "ManualPoints skip: event history {Id} has no loot and no existing column.", eventHistoryId);
            return;
        }
        if (isNewColumn)
        {
            columnIndex = FindFirstEmptyColumn(headerRow, FirstEventColumn);
        }
        var colLetter = ColumnIndexToLetter(columnIndex);

        if (isNewColumn)
        {
            var totalFormula = $"=SUM({colLetter}{MemberStartRow}:{colLetter}{TotalFormulaEndRow})";
            await _sheets.WriteAsync(
                linkshell.Id,
                spreadsheetId,
                $"{tab}!{colLetter}1:{colLetter}4",
                new List<IList<object>>
                {
                    new List<object> { header },
                    new List<object> { ColumnSubheader },
                    new List<object> { monthYear },
                    new List<object> { totalFormula },
                },
                cancellationToken);
        }
        else
        {
            // Self-heal the row-2 subheader the Main-tab rollup keys off
            // (matches the ToD recompute).
            await _sheets.WriteAsync(
                linkshell.Id,
                spreadsheetId,
                $"{tab}!{colLetter}2",
                new List<IList<object>> { new List<object> { ColumnSubheader } },
                cancellationToken);
        }

        var memberRows = await _sheets.ReadAsync(linkshell.Id, spreadsheetId, $"{tab}!A{MemberStartRow}:A", cancellationToken)
            ?? new List<IList<object>>();
        var nameToRow = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < memberRows.Count; i++)
        {
            var name = memberRows[i].Count > 0 ? memberRows[i][0]?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(name)) nameToRow.TryAdd(name!, MemberStartRow + i);
        }
        var nextAppendRow = MemberStartRow + memberRows.Count;

        // Existing values in this column so a recompute can BLANK characters
        // who no longer owe (refunded / deleted / edited to someone else).
        IList<IList<object>> existingCol = new List<IList<object>>();
        if (!isNewColumn && memberRows.Count > 0)
        {
            existingCol = await _sheets.ReadAsync(
                linkshell.Id,
                spreadsheetId,
                $"{tab}!{colLetter}{MemberStartRow}:{colLetter}{MemberStartRow + memberRows.Count - 1}",
                cancellationToken) ?? new List<IList<object>>();
        }

        foreach (var pair in totalsByCharacter)
        {
            if (!nameToRow.TryGetValue(pair.Key, out var row))
            {
                row = nextAppendRow++;
                await _sheets.WriteAsync(
                    linkshell.Id, spreadsheetId, $"{tab}!A{row}",
                    new List<IList<object>> { new List<object> { pair.Key } },
                    cancellationToken);
                nameToRow[pair.Key] = row;
            }
            await _sheets.WriteAsync(
                linkshell.Id, spreadsheetId, $"{tab}!{colLetter}{row}",
                new List<IList<object>> { new List<object> { pair.Value } },
                cancellationToken);
        }

        for (var i = 0; i < existingCol.Count; i++)
        {
            var name = memberRows.Count > i && memberRows[i].Count > 0
                ? memberRows[i][0]?.ToString()
                : null;
            var hadValue = existingCol[i].Count > 0
                && !string.IsNullOrWhiteSpace(existingCol[i][0]?.ToString());
            if (!string.IsNullOrWhiteSpace(name)
                && hadValue
                && !totalsByCharacter.ContainsKey(name!.Trim()))
            {
                await _sheets.WriteAsync(
                    linkshell.Id, spreadsheetId, $"{tab}!{colLetter}{MemberStartRow + i}",
                    new List<IList<object>> { new List<object> { string.Empty } },
                    cancellationToken);
            }
        }

        _logger.LogInformation(
            "ManualPoints event-loot recompute: {Header} -> {Tab}!{Col} ({Members} member rows, newColumn={New}).",
            header, tab, colLetter, totalsByCharacter.Count, isNewColumn);
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
                new List<object> { ColumnSubheader },
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
