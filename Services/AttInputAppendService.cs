using System.Globalization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Appends rows to the user's Google Sheet "AttInput" tab so the sheet's
// existing formula chain (AttInput -> Tally -> Main!F -> Main!C) keeps
// working. Each public method is idempotent via an AttInputAppendedAt
// timestamp on the source row; if the stamp is non-null, the method
// short-circuits without re-appending.
//
// Column layout (matches the user's existing sheet):
//   A Player | B Jobs | C Date | D Time + UTC offset | E (blank)
//   F Location | G (blank) | H Player Name (duplicate)
//   I Camp Window | J DKP | K Entry Type
public sealed class AttInputAppendService
{
    private const string DefaultTabName = "AttInput";

    private readonly ApplicationDbContext _db;
    private readonly GoogleSheetsSyncService _sheets;
    private readonly ILogger<AttInputAppendService> _logger;

    public AttInputAppendService(
        ApplicationDbContext db,
        GoogleSheetsSyncService sheets,
        ILogger<AttInputAppendService> logger)
    {
        _db = db;
        _sheets = sheets;
        _logger = logger;
    }

    public async Task AppendSnapshotAsync(int snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await _db.AttendanceSnapshots
            .Include(s => s.Entries)
            .Include(s => s.LinkedEvent)
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null)
        {
            _logger.LogDebug("AttInput skip: snapshot {SnapshotId} not found.", snapshotId);
            return;
        }
        if (snapshot.AttInputAppendedAt.HasValue)
        {
            _logger.LogDebug("AttInput skip: snapshot {SnapshotId} already appended at {When}.", snapshotId, snapshot.AttInputAppendedAt);
            return;
        }

        var linkshell = await LoadConfiguredLinkshellAsync(snapshot.LinkshellId, cancellationToken);
        if (linkshell is null) return;

        var entryType = snapshot.LinkedEvent?.AttInputEntryType ?? linkshell.AttInputDefaultEntryType;
        if (string.IsNullOrWhiteSpace(entryType))
        {
            _logger.LogDebug("AttInput skip: snapshot {SnapshotId} has no entry type (link {LinkedEventId}, ls default {Default}).",
                snapshotId, snapshot.LinkedEventId, linkshell.AttInputDefaultEntryType);
            return;
        }

        var dkpPerEntry = snapshot.LinkedEvent?.DkpPerHour ?? 0;
        var rows = new List<IList<object>>(snapshot.Entries.Count);
        foreach (var entry in snapshot.Entries.OrderBy(e => e.CharacterName, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(BuildRow(
                playerName: entry.CharacterName,
                jobs: BuildJobsCell(entry.MainJob, entry.MainJobLevel, entry.SubJob, entry.SubJobLevel),
                whenUtc: snapshot.CapturedAtUtc,
                utcOffset: snapshot.UtcOffset,
                location: entry.Zone,
                campWindow: 1,
                dkp: dkpPerEntry,
                entryType: entryType));
        }

        if (rows.Count == 0)
        {
            _logger.LogDebug("AttInput skip: snapshot {SnapshotId} has no entries.", snapshotId);
            return;
        }

        await AppendRowsAsync(linkshell, rows, cancellationToken);

        snapshot.AttInputAppendedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("AttInput append: snapshot {SnapshotId} -> {Count} rows.", snapshotId, rows.Count);
    }

    public async Task AppendEventWindowAsync(int windowId, CancellationToken cancellationToken)
    {
        var window = await _db.EventAttendanceWindows
            .Include(w => w.Event)
            .Include(w => w.Attendees).ThenInclude(a => a.AppUserEvent)
            .FirstOrDefaultAsync(w => w.Id == windowId, cancellationToken);
        if (window is null || window.Event is null)
        {
            _logger.LogDebug("AttInput skip: window {WindowId} not found.", windowId);
            return;
        }
        if (window.AttInputAppendedAt.HasValue)
        {
            _logger.LogDebug("AttInput skip: window {WindowId} already appended.", windowId);
            return;
        }

        var linkshell = await LoadConfiguredLinkshellAsync(window.Event.LinkshellId, cancellationToken);
        if (linkshell is null) return;

        if (string.IsNullOrWhiteSpace(window.Event.AttInputEntryType))
        {
            _logger.LogDebug("AttInput skip: event {EventId} has no AttInputEntryType.", window.EventId);
            return;
        }

        var dkpPerWindow = window.DkpAmount ?? window.Event.DkpPerHour ?? 0;
        var rows = new List<IList<object>>(window.Attendees.Count);
        foreach (var attendee in window.Attendees)
        {
            if (attendee.AppUserEvent is null) continue;
            rows.Add(BuildRow(
                playerName: attendee.AppUserEvent.CharacterName,
                jobs: BuildJobsCell(attendee.AppUserEvent.JobName, null, attendee.AppUserEvent.SubJobName, null),
                whenUtc: window.PostedAt,
                utcOffset: null,
                location: attendee.Zone ?? window.Event.EventLocation,
                campWindow: window.SequenceNumber,
                dkp: dkpPerWindow,
                entryType: window.Event.AttInputEntryType!));
        }

        if (rows.Count == 0)
        {
            _logger.LogDebug("AttInput skip: window {WindowId} has no attendees.", windowId);
            return;
        }

        await AppendRowsAsync(linkshell, rows, cancellationToken);

        window.AttInputAppendedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("AttInput append: window {WindowId} -> {Count} rows.", windowId, rows.Count);
    }

    public async Task AppendEventCloseAsync(int eventId, CancellationToken cancellationToken)
    {
        var evt = await _db.Events
            .Include(e => e.AppUserEvents)
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (evt is null)
        {
            _logger.LogDebug("AttInput skip: event {EventId} not found.", eventId);
            return;
        }
        if (evt.AttInputAppendedAt.HasValue)
        {
            _logger.LogDebug("AttInput skip: event {EventId} already appended.", eventId);
            return;
        }
        if (string.IsNullOrWhiteSpace(evt.AttInputEntryType))
        {
            _logger.LogDebug("AttInput skip: event {EventId} has no AttInputEntryType.", eventId);
            return;
        }

        var linkshell = await LoadConfiguredLinkshellAsync(evt.LinkshellId, cancellationToken);
        if (linkshell is null) return;

        var startUtc = evt.CommencementStartTime ?? evt.StartTime ?? DateTime.UtcNow;
        var endUtc = evt.EndTime ?? DateTime.UtcNow;
        var durationHours = Math.Max(0, (endUtc - startUtc).TotalHours);
        var dkpPerHour = evt.DkpPerHour ?? 0;

        var rows = new List<IList<object>>(evt.AppUserEvents.Count);
        foreach (var participation in evt.AppUserEvents)
        {
            if (participation.IsVerified != true) continue;
            var perAttendeeHours = participation.Duration ?? durationHours;
            var dkp = dkpPerHour * perAttendeeHours;
            rows.Add(BuildRow(
                playerName: participation.CharacterName,
                jobs: BuildJobsCell(participation.JobName, null, participation.SubJobName, null),
                whenUtc: endUtc,
                utcOffset: null,
                location: evt.EventLocation,
                campWindow: 1,
                dkp: dkp,
                entryType: evt.AttInputEntryType!));
        }

        if (rows.Count == 0)
        {
            _logger.LogDebug("AttInput skip: event {EventId} has no verified attendees.", eventId);
            return;
        }

        await AppendRowsAsync(linkshell, rows, cancellationToken);

        evt.AttInputAppendedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("AttInput append: event {EventId} close -> {Count} rows.", eventId, rows.Count);
    }

    // ---- internals ----

    private async Task<Linkshell?> LoadConfiguredLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var linkshell = await _db.Linkshells.FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            _logger.LogDebug("AttInput skip: linkshell {LinkshellId} not found.", linkshellId);
            return null;
        }
        if (!linkshell.SheetSyncEnabled)
        {
            _logger.LogDebug("AttInput skip: linkshell {LinkshellId} has SheetSyncEnabled=false.", linkshellId);
            return null;
        }
        if (string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId))
        {
            _logger.LogDebug("AttInput skip: linkshell {LinkshellId} has no spreadsheet configured.", linkshellId);
            return null;
        }
        if (string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc))
        {
            _logger.LogDebug("AttInput skip: linkshell {LinkshellId} has no Google OAuth token.", linkshellId);
            return null;
        }
        return linkshell;
    }

    private async Task AppendRowsAsync(Linkshell linkshell, IList<IList<object>> rows, CancellationToken cancellationToken)
    {
        var tab = string.IsNullOrWhiteSpace(linkshell.AttInputTabName) ? DefaultTabName : linkshell.AttInputTabName!;
        var range = $"{tab}!A:K";
        await _sheets.AppendAsync(linkshell.Id, linkshell.GoogleSpreadsheetId!, range, rows, cancellationToken);
    }

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
        var time = whenUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var offsetTag = string.IsNullOrWhiteSpace(utcOffset) ? "UTC+0000" : utcOffset!;
        return new List<object>
        {
            playerName ?? string.Empty,            // A Player
            jobs ?? string.Empty,                  // B Jobs
            date,                                  // C Date REQ
            $"{time} {offsetTag}",                 // D Time + UTC offset
            string.Empty,                          // E (blank)
            location ?? string.Empty,              // F Location
            string.Empty,                          // G (blank)
            playerName ?? string.Empty,            // H Player Name (duplicate per user's layout)
            campWindow,                            // I Camp Window
            dkp,                                   // J DKP
            entryType,                             // K Entry Type
        };
    }

    private static string? BuildJobsCell(string? mainJob, int? mainLevel, string? subJob, int? subLevel)
    {
        if (string.IsNullOrWhiteSpace(mainJob) && string.IsNullOrWhiteSpace(subJob)) return null;
        var main = string.IsNullOrWhiteSpace(mainJob)
            ? ""
            : mainLevel.HasValue ? $"{mainJob}{mainLevel}" : mainJob!;
        var sub = string.IsNullOrWhiteSpace(subJob)
            ? ""
            : subLevel.HasValue ? $"{subJob}{subLevel}" : subJob!;
        if (string.IsNullOrEmpty(sub)) return main;
        if (string.IsNullOrEmpty(main)) return sub;
        return $"{main}/{sub}";
    }
}
