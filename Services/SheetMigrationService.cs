using System.Globalization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

public sealed class SheetMigrationService
{
    private const string DefaultReadRange = "Main!B7:C200";

    private readonly ApplicationDbContext _db;
    private readonly GoogleSheetsSyncService _sheets;
    private readonly ILogger<SheetMigrationService> _logger;

    public SheetMigrationService(
        ApplicationDbContext db,
        GoogleSheetsSyncService sheets,
        ILogger<SheetMigrationService> logger)
    {
        _db = db;
        _sheets = sheets;
        _logger = logger;
    }

    public async Task<ImportPreview> BuildPreviewAsync(int linkshellId, string spreadsheetId, string? readRange, CancellationToken cancellationToken)
    {
        var range = string.IsNullOrWhiteSpace(readRange) ? DefaultReadRange : readRange!;
        var raw = await _sheets.ReadAsync(linkshellId, spreadsheetId, range, cancellationToken)
                  ?? new List<IList<object>>();

        var rows = ExtractRows(raw);

        var existingMembers = await _db.AppUserLinkshells
            .AsNoTracking()
            .Where(m => m.LinkshellId == linkshellId)
            .Select(m => new { m.Id, m.CharacterName, m.LinkshellDkp })
            .ToListAsync(cancellationToken);

        var byName = existingMembers
            .Where(m => !string.IsNullOrWhiteSpace(m.CharacterName))
            .ToDictionary(m => m.CharacterName!.Trim(), m => m, StringComparer.OrdinalIgnoreCase);

        var preview = new ImportPreview { Range = range };

        foreach (var row in rows)
        {
            if (string.Equals(row.Name, "TOTAL", StringComparison.OrdinalIgnoreCase)) continue;

            if (byName.TryGetValue(row.Name, out var existing))
            {
                preview.Updates.Add(new ImportUpdate(existing.Id, row.Name, existing.LinkshellDkp ?? 0, row.Dkp, existing.LinkshellDkp == row.Dkp));
            }
            else
            {
                preview.MissingMembers.Add(new ImportMissing(row.Name, row.Dkp));
            }
        }

        return preview;
    }

    public async Task<ImportResult> CommitAsync(int linkshellId, string spreadsheetId, string? readRange, CancellationToken cancellationToken)
    {
        var range = string.IsNullOrWhiteSpace(readRange) ? DefaultReadRange : readRange!;
        var raw = await _sheets.ReadAsync(linkshellId, spreadsheetId, range, cancellationToken)
                  ?? new List<IList<object>>();

        var rows = ExtractRows(raw);

        var members = await _db.AppUserLinkshells
            .Where(m => m.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        var byName = members
            .Where(m => !string.IsNullOrWhiteSpace(m.CharacterName))
            .ToDictionary(m => m.CharacterName!.Trim(), m => m, StringComparer.OrdinalIgnoreCase);

        var result = new ImportResult();
        var now = DateTime.UtcNow;

        foreach (var row in rows)
        {
            if (string.Equals(row.Name, "TOTAL", StringComparison.OrdinalIgnoreCase)) continue;

            if (!byName.TryGetValue(row.Name, out var member))
            {
                var placeholder = new AppUserLinkshell
                {
                    AppUserId = null,
                    LinkshellId = linkshellId,
                    CharacterName = row.Name,
                    Status = "Unclaimed",
                    LinkshellDkp = row.Dkp,
                };
                _db.AppUserLinkshells.Add(placeholder);
                byName[row.Name] = placeholder;

                _db.DkpLedgerEntries.Add(new DkpLedgerEntry
                {
                    AppUserId = null,
                    LinkshellId = linkshellId,
                    EntryType = "SheetImport",
                    Amount = row.Dkp,
                    Sequence = 1,
                    OccurredAt = now,
                    CharacterName = row.Name,
                    Details = $"Placeholder created from Google Sheet {spreadsheetId} ({range}) with starting balance {row.Dkp}.",
                });
                result.Created++;
                continue;
            }

            var oldValue = member.LinkshellDkp ?? 0;
            if (Math.Abs(oldValue - row.Dkp) < 0.0001)
            {
                result.Unchanged++;
                continue;
            }

            var delta = row.Dkp - oldValue;
            member.LinkshellDkp = row.Dkp;

            _db.DkpLedgerEntries.Add(new DkpLedgerEntry
            {
                AppUserId = member.AppUserId,
                LinkshellId = linkshellId,
                EntryType = "SheetImport",
                Amount = delta,
                Sequence = 1,
                OccurredAt = now,
                CharacterName = member.CharacterName,
                Details = $"Imported from Google Sheet {spreadsheetId} ({range}). Old: {oldValue}, New: {row.Dkp}.",
            });

            result.Updated++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<ReconciliationSheetData> ReadForReconciliationAsync(int linkshellId, string spreadsheetId, string? tabName, CancellationToken cancellationToken)
    {
        var tab = string.IsNullOrWhiteSpace(tabName) ? "Main" : tabName!;
        var headerRange = $"{tab}!B6:L6";
        var dataRange = $"{tab}!B8:L200";

        var headersRaw = await _sheets.ReadAsync(linkshellId, spreadsheetId, headerRange, cancellationToken)
                        ?? new List<IList<object>>();
        var dataRaw = await _sheets.ReadAsync(linkshellId, spreadsheetId, dataRange, cancellationToken)
                     ?? new List<IList<object>>();

        var headers = new List<string>();
        if (headersRaw.Count > 0)
        {
            foreach (var cell in headersRaw[0])
            {
                headers.Add(cell?.ToString()?.Trim() ?? string.Empty);
            }
        }

        var rows = new List<ReconciliationSheetRow>();
        foreach (var raw in dataRaw)
        {
            if (raw.Count < 2) continue;
            var name = raw[0]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (string.Equals(name, "TOTAL", StringComparison.OrdinalIgnoreCase)) continue;

            if (!TryParseNumber(raw[1], out var dkp)) dkp = 0;

            var cells = new List<string>(raw.Count);
            for (var i = 0; i < raw.Count; i++)
            {
                cells.Add(raw[i]?.ToString() ?? string.Empty);
            }

            rows.Add(new ReconciliationSheetRow(name, dkp, cells));
        }

        return new ReconciliationSheetData(headers, rows);
    }

    private static List<SheetRow> ExtractRows(IList<IList<object>> raw)
    {
        var list = new List<SheetRow>(raw.Count);
        foreach (var row in raw)
        {
            if (row.Count < 2) continue;
            var name = row[0]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            if (!TryParseNumber(row[1], out var dkp)) continue;

            list.Add(new SheetRow(name, dkp));
        }
        return list;
    }

    private static bool TryParseNumber(object? cell, out double value)
    {
        value = 0;
        if (cell is null) return false;
        var text = cell.ToString();
        if (string.IsNullOrWhiteSpace(text)) return false;
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }

    private readonly record struct SheetRow(string Name, double Dkp);
}

public sealed class ImportPreview
{
    public string Range { get; set; } = string.Empty;
    public List<ImportUpdate> Updates { get; } = new();
    public List<ImportMissing> MissingMembers { get; } = new();
}

public sealed record ImportUpdate(int MembershipId, string CharacterName, double CurrentDkp, double SheetDkp, bool Matches);

public sealed record ImportMissing(string CharacterName, double SheetDkp);

public sealed class ImportResult
{
    public int Updated { get; set; }
    public int Created { get; set; }
    public int Unchanged { get; set; }
    public List<string> Skipped { get; } = new();
}

public sealed record ReconciliationSheetData(IReadOnlyList<string> Headers, IReadOnlyList<ReconciliationSheetRow> Rows);

public sealed record ReconciliationSheetRow(string CharacterName, double Dkp, IReadOnlyList<string> Cells);
