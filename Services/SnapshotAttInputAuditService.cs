using System.Globalization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Applies DKP audits for snapshot/window-event rows by editing the source
// AttInput row directly. This is intentionally limited to SnapshotEarned
// ledger entries; other audits continue to write ManualPoints deltas.
public sealed class SnapshotAttInputAuditService
{
    private const string DefaultTabName = "AttInput";

    private readonly ApplicationDbContext _db;
    private readonly GoogleSheetsSyncService _sheets;

    public SnapshotAttInputAuditService(ApplicationDbContext db, GoogleSheetsSyncService sheets)
    {
        _db = db;
        _sheets = sheets;
    }

    public async Task<int> CorrectSnapshotEarnedRowAsync(
        DkpLedgerEntry original,
        double correctedAmount,
        CancellationToken cancellationToken)
    {
        if (!IsSnapshotEarnedEntry(original))
        {
            throw new InvalidOperationException("Only snapshot-earned ledger entries can update AttInput directly.");
        }

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == original.LinkshellId, cancellationToken)
            ?? throw new InvalidOperationException("Linkshell not found for the selected snapshot entry.");

        if (!linkshell.SheetSyncEnabled ||
            string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId) ||
            string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            throw new InvalidOperationException("This linkshell is not connected to Google Sheets.");
        }

        var tab = string.IsNullOrWhiteSpace(linkshell.AttInputTabName) ? DefaultTabName : linkshell.AttInputTabName!;
        var rowNumber = original.AttInputRowNumber;
        if (!rowNumber.HasValue || rowNumber.Value <= 0)
        {
            rowNumber = await LocateAttInputRowAsync(linkshell, tab, original, cancellationToken);
            if (rowNumber.HasValue)
            {
                original.AttInputRowNumber = rowNumber.Value;
            }
        }

        if (!rowNumber.HasValue || rowNumber.Value <= 0)
        {
            throw new InvalidOperationException("Could not find the original AttInput row for this snapshot entry.");
        }

        await _sheets.WriteAsync(
            linkshell.Id,
            linkshell.GoogleSpreadsheetId!,
            $"{tab}!J{rowNumber.Value}",
            new List<IList<object>> { new List<object> { correctedAmount } },
            cancellationToken);

        return rowNumber.Value;
    }

    public async Task<SnapshotMissingMemberRowResult> AddMissingSnapshotMemberRowAsync(
        int linkshellId,
        int windowEventId,
        AppUserLinkshell targetMembership,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetMembership.AppUserId))
        {
            throw new InvalidOperationException("The selected member has no user account.");
        }

        var existing = await _db.DkpLedgerEntries.AnyAsync(entry =>
            entry.LinkshellId == linkshellId &&
            entry.AppUserId == targetMembership.AppUserId &&
            entry.SourceWindowEventId == windowEventId &&
            entry.EntryType == "SnapshotEarned",
            cancellationToken);
        if (existing)
        {
            throw new InvalidOperationException("That member already has DKP recorded for the selected snapshot entry.");
        }

        var windowEvent = await _db.WindowEvents
            .AsNoTracking()
            .Include(w => w.Snapshots).ThenInclude(s => s.Entries)
            .FirstOrDefaultAsync(w => w.Id == windowEventId && w.LinkshellId == linkshellId, cancellationToken)
            ?? throw new InvalidOperationException("The selected snapshot entry was not found.");

        if (!windowEvent.PostedToSheetAt.HasValue ||
            !windowEvent.DkpAmount.HasValue ||
            !WindowEventEntryTypes.IsValid(windowEvent.EntryType))
        {
            throw new InvalidOperationException("The selected snapshot entry has not been posted to the DKP sheet.");
        }

        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken)
            ?? throw new InvalidOperationException("Linkshell not found for the selected snapshot entry.");

        if (!linkshell.SheetSyncEnabled ||
            string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId) ||
            string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            throw new InvalidOperationException("This linkshell is not connected to Google Sheets.");
        }

        var activeSnapshots = windowEvent.Snapshots
            .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active)
            .OrderByDescending(s => s.CapturedAtUtc)
            .ToList();
        var representativeSnapshot = activeSnapshots.FirstOrDefault();
        var occurredAt = representativeSnapshot?.CapturedAtUtc ?? windowEvent.LastCapturedAtUtc;
        var utcOffset = representativeSnapshot?.UtcOffset;
        var primaryZone = activeSnapshots
            .SelectMany(s => s.Entries)
            .Where(e => !string.IsNullOrWhiteSpace(e.Zone))
            .GroupBy(e => e.Zone!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .FirstOrDefault();

        var tab = string.IsNullOrWhiteSpace(linkshell.AttInputTabName) ? DefaultTabName : linkshell.AttInputTabName!;
        var row = BuildRow(
            playerName: targetMembership.CharacterName,
            jobs: null,
            whenUtc: occurredAt,
            utcOffset: utcOffset,
            location: primaryZone,
            campWindow: 1,
            dkp: windowEvent.DkpAmount.Value,
            entryType: windowEvent.EntryType!);

        var appendResponse = await _sheets.AppendAsync(
            linkshell.Id,
            linkshell.GoogleSpreadsheetId!,
            $"{tab}!A:K",
            new List<IList<object>> { row },
            cancellationToken);

        TryGetFirstAppendedRow(appendResponse?.Updates?.UpdatedRange, out var attInputRowNumber);
        return new SnapshotMissingMemberRowResult(
            windowEvent.Id,
            windowEvent.DkpAmount.Value,
            attInputRowNumber > 0 ? attInputRowNumber : null,
            occurredAt,
            string.IsNullOrWhiteSpace(windowEvent.Name) ? "Window Event" : windowEvent.Name,
            windowEvent.EntryType,
            primaryZone,
            windowEvent.FirstCapturedAtUtc,
            windowEvent.LastCapturedAtUtc);
    }

    public static bool IsSnapshotEarnedEntry(DkpLedgerEntry entry)
        => entry.EntryType == "SnapshotEarned" && entry.SourceWindowEventId.HasValue;

    private static IList<object> BuildRow(
        string? playerName,
        string? jobs,
        DateTime whenUtc,
        string? utcOffset,
        string? location,
        int campWindow,
        double dkp,
        string entryType)
    {
        var date = whenUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var time = whenUtc.ToString("h:mm:ss tt", CultureInfo.InvariantCulture);
        string offsetTag;
        if (string.IsNullOrWhiteSpace(utcOffset))
        {
            offsetTag = "UTC+0000";
        }
        else if (utcOffset.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
        {
            offsetTag = utcOffset;
        }
        else
        {
            offsetTag = "UTC" + utcOffset;
        }

        return new List<object>
        {
            playerName ?? string.Empty,
            jobs ?? string.Empty,
            date,
            time,
            offsetTag,
            location ?? string.Empty,
            string.Empty,
            playerName ?? string.Empty,
            campWindow,
            dkp,
            entryType,
        };
    }

    private static bool TryGetFirstAppendedRow(string? updatedRange, out int rowNumber)
    {
        rowNumber = 0;
        if (string.IsNullOrWhiteSpace(updatedRange)) return false;

        var bangIndex = updatedRange.LastIndexOf('!');
        var rangePart = bangIndex >= 0 ? updatedRange[(bangIndex + 1)..] : updatedRange;
        var match = System.Text.RegularExpressions.Regex.Match(rangePart, @"^[A-Z]+(?<row>\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["row"].Value, out rowNumber);
    }

    private async Task<int?> LocateAttInputRowAsync(
        Linkshell linkshell,
        string tab,
        DkpLedgerEntry original,
        CancellationToken cancellationToken)
    {
        var rows = await _sheets.ReadAsync(
            linkshell.Id,
            linkshell.GoogleSpreadsheetId!,
            $"{tab}!A:K",
            cancellationToken);
        if (rows is null || rows.Count == 0)
        {
            return null;
        }

        var expectedDate = original.OccurredAt.Date;
        var expectedTime = original.OccurredAt.TimeOfDay;
        var bestRow = 0;
        var bestScore = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var score = ScoreRow(row, original, expectedDate, expectedTime);
            if (score > bestScore)
            {
                bestScore = score;
                bestRow = i + 1;
            }
        }

        // Character + entry type + date + time should be unique for a posted
        // snapshot row. Zone/amount only add confidence because either can be
        // blank or already corrected.
        return bestScore >= 190 ? bestRow : null;
    }

    private static int ScoreRow(
        IList<object> row,
        DkpLedgerEntry original,
        DateTime expectedDate,
        TimeSpan expectedTime)
    {
        var score = 0;
        var characterName = Cell(row, 0);
        var duplicateName = Cell(row, 7);
        if (!string.IsNullOrWhiteSpace(original.CharacterName) &&
            (EqualsCell(characterName, original.CharacterName) || EqualsCell(duplicateName, original.CharacterName)))
        {
            score += 100;
        }

        if (EqualsCell(Cell(row, 10), original.EventType))
        {
            score += 50;
        }

        if (TryParseDate(Cell(row, 2), out var rowDate) && rowDate.Date == expectedDate)
        {
            score += 20;
        }

        if (TryParseTime(Cell(row, 3), out var rowTime) &&
            Math.Abs((rowTime - expectedTime).TotalSeconds) < 1)
        {
            score += 20;
        }

        if (!string.IsNullOrWhiteSpace(original.EventLocation) && EqualsCell(Cell(row, 5), original.EventLocation))
        {
            score += 5;
        }

        if (double.TryParse(Cell(row, 9), NumberStyles.Float, CultureInfo.InvariantCulture, out var rowAmount) &&
            Math.Abs(rowAmount - original.Amount) < 0.0001)
        {
            score += 5;
        }

        return score;
    }

    private static string? Cell(IList<object> row, int index)
        => row.Count > index ? row[index]?.ToString() : null;

    private static bool EqualsCell(string? actual, string? expected)
        => !string.IsNullOrWhiteSpace(actual) &&
           !string.IsNullOrWhiteSpace(expected) &&
           string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TryParseDate(string? value, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date) ||
               DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeUniversal, out date);
    }

    private static bool TryParseTime(string? value, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime) ||
            DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeUniversal, out dateTime))
        {
            time = dateTime.TimeOfDay;
            return true;
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out time) ||
               TimeSpan.TryParse(value, CultureInfo.CurrentCulture, out time);
    }
}

public sealed record SnapshotMissingMemberRowResult(
    int WindowEventId,
    double Amount,
    int? AttInputRowNumber,
    DateTime OccurredAt,
    string? EventName,
    string? EventType,
    string? EventLocation,
    DateTime? EventStartTime,
    DateTime? EventEndTime);
