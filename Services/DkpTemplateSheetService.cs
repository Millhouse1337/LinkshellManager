using System.Globalization;
using Google.Apis.Sheets.v4.Data;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// The canonical "generic DKP template": one styled tab on the linkshell's
// connected Google Sheet, with columns
//   Member Name | Alt 1 | Alt 2 | Current DKP | Total DKP | Total DKP Spent
// that the app can EXPORT (push the linkshell's DKP out) and IMPORT (a linkshell
// reformats a tab to match, then pulls it back in). Lifetime totals come from
// the DkpLedgerEntry ledger plus a per-member seed captured at import time, so a
// migrating linkshell carries its history without re-recording every past
// transaction. Reuses GoogleSheetsSyncService for all Sheets I/O and DkpRounding
// for fractional snapping.
public sealed class DkpTemplateSheetService
{
    public const string DefaultTabName = "LSM DKP";

    // Reconciliation rows (importing a balance, seeding) are NOT real earns or
    // spends, so they never count toward the lifetime Total / Total Spent.
    private const string TemplateImportEntryType = "TemplateImport";
    private static readonly HashSet<string> ReconciliationEntryTypes =
        new(StringComparer.OrdinalIgnoreCase) { TemplateImportEntryType, "SheetImport" };

    private readonly ApplicationDbContext _db;
    private readonly GoogleSheetsSyncService _sheets;

    public DkpTemplateSheetService(ApplicationDbContext db, GoogleSheetsSyncService sheets)
    {
        _db = db;
        _sheets = sheets;
    }

    public static string ResolveTabName(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultTabName : configured!.Trim();

    // ---- Export: app -> styled template tab ---------------------------------

    public async Task<DkpTemplateExportResult> ExportAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var linkshell = await _db.Linkshells.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken)
            ?? throw new InvalidOperationException("Linkshell not found.");
        if (string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId))
        {
            throw new InvalidOperationException("Set the spreadsheet ID before exporting.");
        }

        var spreadsheetId = linkshell.GoogleSpreadsheetId!;
        var tab = ResolveTabName(linkshell.DkpTemplateTabName);
        var step = DkpRounding.StepFor(linkshell.DkpRoundingIncrement);

        var members = await _db.AppUserLinkshells
            .AsNoTracking()
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId)
            .OrderBy(m => m.CharacterName)
            .ToListAsync(cancellationToken);

        var ledgerByUser = await LoadLedgerByUserAsync(linkshellId, cancellationToken);
        // AppUserId -> DKP locked in bids the member is currently winning, so
        // Biddable DKP = Current − committed (the app's "available DKP").
        var committedByUser = await AuctionDkpService.ComputeCommittedDkpByUserAsync(_db, linkshellId, cancellationToken);

        var values = new List<IList<object>>
        {
            new List<object> { $"{linkshell.LinkshellName ?? "Linkshell"} — DKP", "", "", "", "", "", "" },
            new List<object> { "Member Name", "Alt 1", "Alt 2", "Current DKP", "Biddable DKP", "Total DKP", "Total DKP Spent" },
        };

        double sumCurrent = 0, sumBiddable = 0, sumEarned = 0, sumSpent = 0;
        foreach (var m in members)
        {
            var name = m.CharacterName ?? m.AppUser?.CharacterName ?? m.AppUser?.UserName ?? "Unknown";
            var current = DkpRounding.Round(m.LinkshellDkp ?? 0, step);
            var committed = m.AppUserId is not null ? committedByUser.GetValueOrDefault(m.AppUserId, 0) : 0;
            var biddable = DkpRounding.Round((m.LinkshellDkp ?? 0) - committed, step);
            var (earned, spent) = ComputeTotals(m, ledgerByUser, step);
            sumCurrent += current;
            sumBiddable += biddable;
            sumEarned += earned;
            sumSpent += spent;

            values.Add(new List<object>
            {
                name,
                m.AppUser?.AltCharacterName1 ?? string.Empty,
                m.AppUser?.AltCharacterName2 ?? string.Empty,
                current,
                biddable,
                earned,
                spent,
            });
        }

        values.Add(new List<object>
        {
            "TOTAL", "", "",
            DkpRounding.Round(sumCurrent, step),
            DkpRounding.Round(sumBiddable, step),
            DkpRounding.Round(sumEarned, step),
            DkpRounding.Round(sumSpent, step),
        });

        var sheetId = await _sheets.EnsureTabAsync(linkshellId, spreadsheetId, tab, cancellationToken);
        // Clear stale rows (membership may have shrunk) then write the grid from A1.
        await _sheets.ClearAsync(linkshellId, spreadsheetId, $"{tab}!A1:Z2000", cancellationToken);
        await _sheets.WriteAsync(linkshellId, spreadsheetId, $"{tab}!A1", values, cancellationToken);

        // Style: drop any prior banding (AddBanding errors on overlap), then apply.
        var requests = new List<Request>();
        foreach (var bandId in await _sheets.GetBandedRangeIdsAsync(linkshellId, spreadsheetId, sheetId, cancellationToken))
        {
            requests.Add(new Request { DeleteBanding = new DeleteBandingRequest { BandedRangeId = bandId } });
        }
        requests.AddRange(BuildFormattingRequests(sheetId, members.Count));
        await _sheets.BatchUpdateAsync(linkshellId, spreadsheetId, requests, cancellationToken);

        return new DkpTemplateExportResult(tab, members.Count);
    }

    // ---- Import: template tab -> app ----------------------------------------

    public async Task<DkpTemplatePreview> BuildPreviewAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var (linkshell, tab) = await ResolveAsync(linkshellId, cancellationToken);
        var parsed = await ReadTemplateAsync(linkshellId, linkshell.GoogleSpreadsheetId!, tab, cancellationToken);
        var step = DkpRounding.StepFor(linkshell.DkpRoundingIncrement);

        var members = await _db.AppUserLinkshells
            .AsNoTracking()
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        var (byName, byAlt) = BuildMatchers(members.Select(m => (m.Id, m.CharacterName, m.LinkshellDkp,
            m.AppUser?.AltCharacterName1, m.AppUser?.AltCharacterName2)));

        var preview = new DkpTemplatePreview { Tab = tab };
        foreach (var row in parsed)
        {
            var match = Match(byName, byAlt, row.Name);
            preview.Rows.Add(new DkpTemplateImportRow(
                row.Name,
                match?.Id,
                match?.CurrentDkp ?? 0,
                row.Current.HasValue ? DkpRounding.Round(row.Current.Value, step) : (match?.CurrentDkp ?? 0),
                row.Total.HasValue ? DkpRounding.Round(row.Total.Value, step) : 0,
                row.Spent.HasValue ? DkpRounding.Round(row.Spent.Value, step) : 0,
                match is not null));
        }
        return preview;
    }

    public async Task<DkpTemplateImportResult> CommitAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var (linkshell, tab) = await ResolveAsync(linkshellId, cancellationToken);
        var parsed = await ReadTemplateAsync(linkshellId, linkshell.GoogleSpreadsheetId!, tab, cancellationToken);
        var step = DkpRounding.StepFor(linkshell.DkpRoundingIncrement);

        var members = await _db.AppUserLinkshells
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        // Watermark: each member's max ledger Id BEFORE this import. Pre-import
        // real earns/spends (Id <= watermark) are represented by the seed; later
        // activity (Id > watermark, non-reconciliation) adds on top.
        var maxIdByUser = await _db.DkpLedgerEntries
            .Where(e => e.LinkshellId == linkshellId && e.AppUserId != null)
            .GroupBy(e => e.AppUserId!)
            .Select(g => new { AppUserId = g.Key, MaxId = g.Max(e => e.Id) })
            .ToDictionaryAsync(x => x.AppUserId, x => x.MaxId, cancellationToken);

        var membersByName = new Dictionary<string, AppUserLinkshell>(StringComparer.OrdinalIgnoreCase);
        var membersByAlt = new Dictionary<string, AppUserLinkshell>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in members)
        {
            if (!string.IsNullOrWhiteSpace(m.CharacterName)) { membersByName.TryAdd(m.CharacterName!.Trim(), m); }
            if (!string.IsNullOrWhiteSpace(m.AppUser?.AltCharacterName1)) { membersByAlt.TryAdd(m.AppUser!.AltCharacterName1!.Trim(), m); }
            if (!string.IsNullOrWhiteSpace(m.AppUser?.AltCharacterName2)) { membersByAlt.TryAdd(m.AppUser!.AltCharacterName2!.Trim(), m); }
        }

        var result = new DkpTemplateImportResult { Tab = tab };
        var now = DateTime.UtcNow;

        foreach (var row in parsed)
        {
            var member = (membersByName.TryGetValue(row.Name, out var byName) ? byName : null)
                ?? (membersByAlt.TryGetValue(row.Name, out var byAlt) ? byAlt : null);
            if (member is null)
            {
                result.Unmatched.Add(row.Name);
                continue;
            }

            if (row.Current.HasValue)
            {
                var newCurrent = DkpRounding.Round(row.Current.Value, step);
                var oldCurrent = member.LinkshellDkp ?? 0;
                if (Math.Abs(oldCurrent - newCurrent) >= 0.0001)
                {
                    member.LinkshellDkp = newCurrent;
                    // Reconciliation entry (excluded from lifetime totals) for the audit trail.
                    _db.DkpLedgerEntries.Add(new DkpLedgerEntry
                    {
                        AppUserId = member.AppUserId,
                        LinkshellId = linkshellId,
                        EntryType = TemplateImportEntryType,
                        EventName = "DKP Template Import",
                        EventType = "Sheet",
                        Amount = newCurrent - oldCurrent,
                        Sequence = 1,
                        OccurredAt = now,
                        CharacterName = member.CharacterName,
                        Details = $"Current DKP set from template ({oldCurrent} -> {newCurrent}).",
                    });
                }
            }

            if (row.Total.HasValue) { member.SeededDkpEarned = DkpRounding.Round(row.Total.Value, step); }
            if (row.Spent.HasValue) { member.SeededDkpSpent = DkpRounding.Round(row.Spent.Value, step); }
            member.DkpSeedLedgerId = member.AppUserId is not null && maxIdByUser.TryGetValue(member.AppUserId, out var mx) ? mx : 0;

            result.Updated++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    // ---- helpers ------------------------------------------------------------

    private async Task<(Linkshell Linkshell, string Tab)> ResolveAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var linkshell = await _db.Linkshells.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken)
            ?? throw new InvalidOperationException("Linkshell not found.");
        if (string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId))
        {
            throw new InvalidOperationException("Set the spreadsheet ID first.");
        }
        return (linkshell, ResolveTabName(linkshell.DkpTemplateTabName));
    }

    private sealed record LedgerLite(int Id, double Amount, string EntryType);

    private async Task<Dictionary<string, List<LedgerLite>>> LoadLedgerByUserAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var rows = await _db.DkpLedgerEntries
            .AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId && e.AppUserId != null)
            .Select(e => new { e.AppUserId, e.Id, e.Amount, e.EntryType })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(e => e.AppUserId!)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => new LedgerLite(e.Id, e.Amount, e.EntryType ?? string.Empty)).ToList(),
                StringComparer.Ordinal);
    }

    // Lifetime totals = seed + post-watermark, non-reconciliation ledger.
    private static (double Earned, double Spent) ComputeTotals(
        AppUserLinkshell member, Dictionary<string, List<LedgerLite>> ledgerByUser, double step)
    {
        var earned = member.SeededDkpEarned;
        var spent = member.SeededDkpSpent;

        if (member.AppUserId is not null && ledgerByUser.TryGetValue(member.AppUserId, out var entries))
        {
            foreach (var e in entries)
            {
                if (e.Id <= member.DkpSeedLedgerId) { continue; }
                if (ReconciliationEntryTypes.Contains(e.EntryType)) { continue; }
                if (e.Amount > 0) { earned += e.Amount; }
                else if (e.Amount < 0) { spent += -e.Amount; }
            }
        }

        return (DkpRounding.Round(earned, step), DkpRounding.Round(spent, step));
    }

    private async Task<List<ParsedRow>> ReadTemplateAsync(int linkshellId, string spreadsheetId, string tab, CancellationToken cancellationToken)
    {
        var raw = await _sheets.ReadAsync(linkshellId, spreadsheetId, $"{tab}!A1:F1000", unformatted: true, cancellationToken)
                  ?? new List<IList<object>>();
        return ParseTemplate(raw);
    }

    // Header-aware parse: find the header row by its labels, map columns by name
    // (so a reformatted sheet with a different column order still imports), then
    // read the data rows below it.
    private static List<ParsedRow> ParseTemplate(IList<IList<object>> raw)
    {
        var rows = new List<ParsedRow>();
        if (raw.Count == 0) { return rows; }

        int headerIndex = -1, nameCol = -1, currentCol = -1, totalCol = -1, spentCol = -1;
        for (var r = 0; r < raw.Count && headerIndex < 0; r++)
        {
            int n = -1, cur = -1, tot = -1, sp = -1;
            for (var c = 0; c < raw[r].Count; c++)
            {
                var text = Normalize(raw[r][c]);
                if (text.Length == 0) { continue; }
                if (text.Contains("spent")) { sp = c; }
                else if (text.Contains("total")) { tot = c; }
                else if (text.Contains("current")) { cur = c; }
                else if (text.Contains("member") || text.Contains("character") || (text.Contains("name") && !text.Contains("alt"))) { n = c; }
            }
            if (n >= 0 && cur >= 0)
            {
                headerIndex = r; nameCol = n; currentCol = cur; totalCol = tot; spentCol = sp;
            }
        }

        if (headerIndex < 0)
        {
            // No recognizable header — assume the fixed canonical layout (A..F)
            // with a single header row at the top.
            headerIndex = 0; nameCol = 0; currentCol = 3; totalCol = 4; spentCol = 5;
        }

        for (var r = headerIndex + 1; r < raw.Count; r++)
        {
            var name = CellText(raw[r], nameCol);
            if (string.IsNullOrWhiteSpace(name)) { continue; }
            if (string.Equals(name, "TOTAL", StringComparison.OrdinalIgnoreCase)) { continue; }

            rows.Add(new ParsedRow(
                name.Trim(),
                TryNumber(raw[r], currentCol),
                TryNumber(raw[r], totalCol),
                TryNumber(raw[r], spentCol)));
        }
        return rows;
    }

    private static (Dictionary<string, MemberMatch> ByName, Dictionary<string, MemberMatch> ByAlt) BuildMatchers(
        IEnumerable<(int Id, string? CharacterName, double? Dkp, string? Alt1, string? Alt2)> members)
    {
        var byName = new Dictionary<string, MemberMatch>(StringComparer.OrdinalIgnoreCase);
        var byAlt = new Dictionary<string, MemberMatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in members)
        {
            var match = new MemberMatch(m.Id, m.Dkp ?? 0);
            if (!string.IsNullOrWhiteSpace(m.CharacterName)) { byName.TryAdd(m.CharacterName!.Trim(), match); }
            if (!string.IsNullOrWhiteSpace(m.Alt1)) { byAlt.TryAdd(m.Alt1!.Trim(), match); }
            if (!string.IsNullOrWhiteSpace(m.Alt2)) { byAlt.TryAdd(m.Alt2!.Trim(), match); }
        }
        return (byName, byAlt);
    }

    private static MemberMatch? Match(Dictionary<string, MemberMatch> byName, Dictionary<string, MemberMatch> byAlt, string name)
        => byName.TryGetValue(name, out var n) ? n : byAlt.TryGetValue(name, out var a) ? a : null;

    private static string Normalize(object? cell) => (cell?.ToString() ?? string.Empty).Trim().ToLowerInvariant();

    private static string CellText(IList<object> row, int col)
        => col >= 0 && col < row.Count ? (row[col]?.ToString() ?? string.Empty) : string.Empty;

    private static double? TryNumber(IList<object> row, int col)
    {
        if (col < 0 || col >= row.Count) { return null; }
        var text = row[col]?.ToString();
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            || double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out v))
        {
            return v;
        }
        return null;
    }

    private readonly record struct ParsedRow(string Name, double? Current, double? Total, double? Spent);

    private readonly record struct MemberMatch(int Id, double CurrentDkp);

    // ---- styling ------------------------------------------------------------

    private static List<Request> BuildFormattingRequests(int sheetId, int dataRowCount)
    {
        const int colCount = 7;   // Member, Alt1, Alt2, Current, Biddable, Total, Total Spent
        var totalsRow = 2 + dataRowCount;          // 0-based: title(0), header(1), data(2..), totals
        var gridEnd = totalsRow + 1;

        var titleBg = new Color { Red = 0.15f, Green = 0.16f, Blue = 0.24f };
        var headerBg = new Color { Red = 0.39f, Green = 0.40f, Blue = 0.95f };
        var totalsBg = new Color { Red = 0.90f, Green = 0.91f, Blue = 0.96f };
        var white = new Color { Red = 1f, Green = 1f, Blue = 1f };
        var band2 = new Color { Red = 0.95f, Green = 0.95f, Blue = 1f };

        GridRange Grid(int r0, int r1, int c0, int c1) => new()
        {
            SheetId = sheetId,
            StartRowIndex = r0,
            EndRowIndex = r1,
            StartColumnIndex = c0,
            EndColumnIndex = c1,
        };

        var requests = new List<Request>
        {
            // Freeze the title + header rows.
            new()
            {
                UpdateSheetProperties = new UpdateSheetPropertiesRequest
                {
                    Properties = new SheetProperties { SheetId = sheetId, GridProperties = new GridProperties { FrozenRowCount = 2 } },
                    Fields = "gridProperties.frozenRowCount",
                },
            },
            // Merge + style the branded title row.
            new() { MergeCells = new MergeCellsRequest { Range = Grid(0, 1, 0, colCount), MergeType = "MERGE_ALL" } },
            new()
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = Grid(0, 1, 0, colCount),
                    Cell = new CellData
                    {
                        UserEnteredFormat = new CellFormat
                        {
                            BackgroundColor = titleBg,
                            HorizontalAlignment = "CENTER",
                            TextFormat = new TextFormat { Bold = true, FontSize = 14, ForegroundColor = white },
                        },
                    },
                    Fields = "userEnteredFormat(backgroundColor,horizontalAlignment,textFormat)",
                },
            },
            // Bold, colored, centered header row.
            new()
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = Grid(1, 2, 0, colCount),
                    Cell = new CellData
                    {
                        UserEnteredFormat = new CellFormat
                        {
                            BackgroundColor = headerBg,
                            HorizontalAlignment = "CENTER",
                            TextFormat = new TextFormat { Bold = true, ForegroundColor = white },
                        },
                    },
                    Fields = "userEnteredFormat(backgroundColor,horizontalAlignment,textFormat)",
                },
            },
            // Number format on the three DKP columns (data + totals).
            new()
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = Grid(2, gridEnd, 3, colCount),
                    Cell = new CellData
                    {
                        UserEnteredFormat = new CellFormat { NumberFormat = new NumberFormat { Type = "NUMBER", Pattern = "0.##" } },
                    },
                    Fields = "userEnteredFormat.numberFormat",
                },
            },
            // Emphasize the TOTAL row.
            new()
            {
                RepeatCell = new RepeatCellRequest
                {
                    Range = Grid(totalsRow, gridEnd, 0, colCount),
                    Cell = new CellData
                    {
                        UserEnteredFormat = new CellFormat { BackgroundColor = totalsBg, TextFormat = new TextFormat { Bold = true } },
                    },
                    Fields = "userEnteredFormat(backgroundColor,textFormat)",
                },
            },
            // Column widths.
            ColumnWidth(sheetId, 0, 1, 180),
            ColumnWidth(sheetId, 1, 3, 140),
            ColumnWidth(sheetId, 3, colCount, 120),
        };

        // Alternating row banding over the data rows (skip when empty).
        if (dataRowCount > 0)
        {
            requests.Add(new Request
            {
                AddBanding = new AddBandingRequest
                {
                    BandedRange = new BandedRange
                    {
                        Range = Grid(2, 2 + dataRowCount, 0, colCount),
                        RowProperties = new BandingProperties { FirstBandColor = white, SecondBandColor = band2 },
                    },
                },
            });
        }

        return requests;
    }

    private static Request ColumnWidth(int sheetId, int startCol, int endCol, int pixels) => new()
    {
        UpdateDimensionProperties = new UpdateDimensionPropertiesRequest
        {
            Range = new DimensionRange { SheetId = sheetId, Dimension = "COLUMNS", StartIndex = startCol, EndIndex = endCol },
            Properties = new DimensionProperties { PixelSize = pixels },
            Fields = "pixelSize",
        },
    };
}

public sealed record DkpTemplateExportResult(string Tab, int MemberCount);

public sealed class DkpTemplatePreview
{
    public string Tab { get; set; } = string.Empty;
    public List<DkpTemplateImportRow> Rows { get; } = new();
    public int MatchedCount => Rows.Count(r => r.Matched);
    public int UnmatchedCount => Rows.Count(r => !r.Matched);
}

public sealed record DkpTemplateImportRow(
    string Name,
    int? MembershipId,
    double OldCurrent,
    double NewCurrent,
    double Total,
    double Spent,
    bool Matched);

public sealed class DkpTemplateImportResult
{
    public string Tab { get; set; } = string.Empty;
    public int Updated { get; set; }
    public List<string> Unmatched { get; } = new();
}
