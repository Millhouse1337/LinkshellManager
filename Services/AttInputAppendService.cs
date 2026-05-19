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
//   A Player | B Jobs | C Date | D Time | E UTC offset
//   F Location | G (blank) | H Player Name (duplicate)
//   I Camp Window | J DKP | K Entry Type
public sealed class AttInputAppendService
{
    private const string DefaultTabName = "AttInput";

    private readonly ApplicationDbContext _db;
    private readonly GoogleSheetsSyncService _sheets;
    private readonly WindowEventDkpLedgerService _windowEventDkpLedger;
    private readonly ILogger<AttInputAppendService> _logger;

    public AttInputAppendService(
        ApplicationDbContext db,
        GoogleSheetsSyncService sheets,
        WindowEventDkpLedgerService windowEventDkpLedger,
        ILogger<AttInputAppendService> logger)
    {
        _db = db;
        _sheets = sheets;
        _windowEventDkpLedger = windowEventDkpLedger;
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

    // Posts a Window Event's combined roster to the linkshell's AttInput tab.
    // Layout per the user's sheet:
    //   * 1 header / separator row carrying the event's name in column A so
    //     each event group is visually delimited
    //   * 1 row per unique active-snapshot character, using the event's
    //     DkpAmount in column J and EntryType in column K
    // Active = SnapshotStatus == Active. Duplicate / Ignored snapshots and
    // their entries are excluded so the sheet matches what the Window Events
    // card shows in the Combined Members table.
    public async Task AppendWindowEventAsync(int windowEventId, CancellationToken cancellationToken)
    {
        var windowEvent = await _db.WindowEvents
            .Include(w => w.Snapshots).ThenInclude(s => s.Entries)
            .Include(w => w.MemberDkpOverrides)
            .FirstOrDefaultAsync(w => w.Id == windowEventId, cancellationToken);
        if (windowEvent is null)
        {
            _logger.LogDebug("AttInput skip: window-event {Id} not found.", windowEventId);
            return;
        }
        if (windowEvent.PostedToSheetAt.HasValue)
        {
            _logger.LogDebug("AttInput skip: window-event {Id} already posted at {When}.",
                windowEventId, windowEvent.PostedToSheetAt);
            return;
        }
        if (!windowEvent.DkpAmount.HasValue)
        {
            _logger.LogDebug("AttInput skip: window-event {Id} has no DkpAmount set.", windowEventId);
            return;
        }
        if (!WindowEventEntryTypes.IsValid(windowEvent.EntryType))
        {
            _logger.LogDebug("AttInput skip: window-event {Id} has invalid EntryType {Type}.",
                windowEventId, windowEvent.EntryType);
            return;
        }

        var linkshell = await LoadConfiguredLinkshellAsync(windowEvent.LinkshellId, cancellationToken);
        if (linkshell is null) return;

        // Build the combined member list: one entry per unique character name
        // from Active snapshots only. Use the most recent snapshot's entry
        // for jobs / zone so the displayed roster matches the card.
        var activeEntries = windowEvent.Snapshots
            .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active)
            .SelectMany(s => s.Entries.Select(e => new { Snapshot = s, Entry = e }))
            .Where(x => !string.IsNullOrWhiteSpace(x.Entry.CharacterName))
            .ToList();

        var combined = activeEntries
            .GroupBy(x => x.Entry.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Snapshot.CapturedAtUtc).First())
            .ToList();

        if (combined.Count == 0)
        {
            _logger.LogDebug("AttInput skip: window-event {Id} has no active snapshot entries.", windowEventId);
            return;
        }

        var rows = new List<IList<object>>(combined.Count + 1);

        // Header / separator row. Keep every formula-sensitive column blank;
        // only column C carries the snapshot/window title, and the entire row
        // is colored after append so it visually separates the event block.
        var displayName = string.IsNullOrWhiteSpace(windowEvent.Name) ? "Window Event" : windowEvent.Name!;
        rows.Add(new List<object>
        {
            string.Empty,
            string.Empty,
            displayName,
            string.Empty,
            string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty,
        });

        var overridesByName = windowEvent.MemberDkpOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
            .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);

        foreach (var item in combined)
        {
            var memberDkp = overridesByName.TryGetValue(item.Entry.CharacterName.Trim(), out var amount)
                ? amount
                : windowEvent.DkpAmount.Value;
            rows.Add(BuildRow(
                playerName: item.Entry.CharacterName,
                jobs: BuildJobsCell(item.Entry.MainJob, item.Entry.MainJobLevel, item.Entry.SubJob, item.Entry.SubJobLevel),
                whenUtc: item.Snapshot.CapturedAtUtc,
                utcOffset: item.Snapshot.UtcOffset,
                location: item.Entry.Zone,
                campWindow: 1,
                dkp: memberDkp,
                entryType: windowEvent.EntryType!));
        }

        var appendResponse = await AppendRowsAsync(linkshell, rows, cancellationToken);
        var firstMemberRowNumber = (int?)null;
        if (TryGetFirstAppendedRow(appendResponse?.Updates?.UpdatedRange, out var headerRowNumber))
        {
            firstMemberRowNumber = headerRowNumber + 1;
            var tab = string.IsNullOrWhiteSpace(linkshell.AttInputTabName) ? DefaultTabName : linkshell.AttInputTabName!;
            await _sheets.FormatRowAsync(
                linkshell.Id,
                linkshell.GoogleSpreadsheetId!,
                tab,
                headerRowNumber,
                red: 1.0f,
                green: 0.93f,
                blue: 0.72f,
                cancellationToken);
        }

        windowEvent.PostedToSheetAt = DateTime.UtcNow;
        windowEvent.FirstAttInputRowNumber = firstMemberRowNumber;
        windowEvent.AttInputRowCount = firstMemberRowNumber.HasValue ? combined.Count : null;
        // Stamp every contributing snapshot so the legacy per-snapshot append
        // path (still wired for SetSnapshotStatus / AttachSnapshot if ever
        // re-enabled) doesn't double-post these rows.
        var nowUtc = DateTime.UtcNow;
        foreach (var snapshot in windowEvent.Snapshots)
        {
            if (snapshot.SnapshotStatus == AttendanceSnapshotStatuses.Active && !snapshot.AttInputAppendedAt.HasValue)
            {
                snapshot.AttInputAppendedAt = nowUtc;
            }
        }
        await _db.SaveChangesAsync(cancellationToken);
        await _windowEventDkpLedger.EnsurePostedWindowEventLedgerEntriesAsync(
            windowEvent.Id,
            cancellationToken,
            firstMemberRowNumber);
        _logger.LogInformation("AttInput append: window-event {Id} -> 1 header + {Count} rows.",
            windowEventId, combined.Count);
    }

    // Re-syncs an already-posted Window Event when an officer edits its
    // DkpAmount or EntryType after the rows are in the sheet. Rewrites
    // columns J (DKP) and K (Entry Type) for the appended range tracked on
    // the WindowEvent and reconciles ledger entry amounts + per-member
    // LinkshellDkp totals by the delta. Header row (FirstAttInputRowNumber - 1)
    // is untouched -- it carries the event title only, no DKP cells.
    public async Task EditPostedWindowEventAsync(int windowEventId, CancellationToken cancellationToken)
    {
        var windowEvent = await _db.WindowEvents
            .Include(w => w.Snapshots).ThenInclude(s => s.Entries)
            .Include(w => w.MemberDkpOverrides)
            .FirstOrDefaultAsync(w => w.Id == windowEventId, cancellationToken);
        if (windowEvent is null)
        {
            _logger.LogDebug("AttInput edit skip: window-event {Id} not found.", windowEventId);
            return;
        }
        if (!windowEvent.PostedToSheetAt.HasValue)
        {
            _logger.LogDebug("AttInput edit skip: window-event {Id} not posted yet.", windowEventId);
            return;
        }
        if (!windowEvent.DkpAmount.HasValue)
        {
            _logger.LogDebug("AttInput edit skip: window-event {Id} has no DkpAmount.", windowEventId);
            return;
        }
        if (!WindowEventEntryTypes.IsValid(windowEvent.EntryType))
        {
            _logger.LogDebug("AttInput edit skip: window-event {Id} invalid EntryType {Type}.",
                windowEventId, windowEvent.EntryType);
            return;
        }

        var linkshell = await LoadConfiguredLinkshellAsync(windowEvent.LinkshellId, cancellationToken);
        if (linkshell is null) return;

        var defaultAmount = windowEvent.DkpAmount.Value;
        var newEntryType = windowEvent.EntryType!;
        var overridesByName = windowEvent.MemberDkpOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
            .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);

        // Recreate the same alphabetical combined-member ordering used at
        // post time so the per-row DKP values line up with the sheet rows
        // we appended originally.
        var combined = windowEvent.Snapshots
            .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active)
            .SelectMany(s => s.Entries.Select(e => new { Snapshot = s, Entry = e }))
            .Where(x => !string.IsNullOrWhiteSpace(x.Entry.CharacterName))
            .GroupBy(x => x.Entry.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Snapshot.CapturedAtUtc).First())
            .ToList();

        double AmountForCharacter(string characterName)
            => overridesByName.TryGetValue(characterName.Trim(), out var v) ? v : defaultAmount;

        if (windowEvent.FirstAttInputRowNumber.HasValue && windowEvent.AttInputRowCount is > 0)
        {
            var tab = string.IsNullOrWhiteSpace(linkshell.AttInputTabName) ? DefaultTabName : linkshell.AttInputTabName!;
            var firstRow = windowEvent.FirstAttInputRowNumber.Value;
            var rowCount = windowEvent.AttInputRowCount!.Value;
            var lastRow = firstRow + rowCount - 1;
            var values = new List<IList<object>>(rowCount);
            for (var i = 0; i < rowCount; i++)
            {
                // If the combined list shrank since the original post (snapshot
                // marked Duplicate/Ignored after the fact), fall through to the
                // event default for those rows so the J cell still gets a sane
                // value -- the sheet row is still physically present.
                var rowAmount = i < combined.Count
                    ? AmountForCharacter(combined[i].Entry.CharacterName)
                    : defaultAmount;
                values.Add(new List<object> { rowAmount, newEntryType });
            }
            var range = $"{tab}!J{firstRow}:K{lastRow}";
            await _sheets.WriteAsync(linkshell.Id, linkshell.GoogleSpreadsheetId!, range, values, cancellationToken);
            _logger.LogInformation(
                "AttInput edit: window-event {Id} -> rewrote J:K rows {First}-{Last}.",
                windowEventId, firstRow, lastRow);
        }
        else
        {
            _logger.LogWarning(
                "AttInput edit: window-event {Id} missing FirstAttInputRowNumber/AttInputRowCount; sheet cells not rewritten.",
                windowEventId);
        }

        var ledgerEntries = await _db.DkpLedgerEntries
            .Where(entry => entry.SourceWindowEventId == windowEventId && entry.AppUserId != null)
            .ToListAsync(cancellationToken);
        if (ledgerEntries.Count > 0)
        {
            var appUserIds = ledgerEntries
                .Select(entry => entry.AppUserId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var memberships = await _db.AppUserLinkshells
                .Where(link => link.LinkshellId == windowEvent.LinkshellId && appUserIds.Contains(link.AppUserId!))
                .ToListAsync(cancellationToken);
            var membershipByAppUserId = memberships
                .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
                .GroupBy(link => link.AppUserId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var entry in ledgerEntries)
            {
                var newAmountForEntry = !string.IsNullOrWhiteSpace(entry.CharacterName)
                    ? AmountForCharacter(entry.CharacterName)
                    : defaultAmount;
                var oldAmount = entry.Amount;
                if (Math.Abs(oldAmount - newAmountForEntry) > 0.0001 &&
                    !string.IsNullOrWhiteSpace(entry.AppUserId) &&
                    membershipByAppUserId.TryGetValue(entry.AppUserId, out var membership))
                {
                    var delta = newAmountForEntry - oldAmount;
                    membership.LinkshellDkp = (membership.LinkshellDkp ?? 0d) + delta;
                }
                entry.Amount = newAmountForEntry;
                entry.EventType = newEntryType;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "AttInput edit: window-event {Id} ledger entries reconciled ({Count}).",
            windowEventId, ledgerEntries.Count);
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

    // Writes a colored label/separator row followed by a single AttInput data
    // row for a POSITIVE DKP ledger entry (manual adjustment / miscellaneous
    // audit). The separator gets the same treatment as the Window Event post
    // so manual additions are visibly delimited from the attendance rows
    // around them. ManualPoints is deductions-only, so anything that credits
    // DKP lands here instead. Idempotent via the entry's SheetAppendedAt
    // stamp (shared with the ManualPoints audit path; the two are
    // sign-disjoint so only one ever claims the stamp).
    //
    // Column choices for a miscellaneous credit (no event/zone/job context):
    //   A/H Player   = entry.CharacterName
    //   B   Jobs     = blank (no job on an adjustment)
    //   C   Date     = entry.OccurredAt (date) -- required by the formula chain
    //   D   Time     = entry.OccurredAt (time)
    //   E   UTC off  = UTC+0000 (OccurredAt is stored UTC)
    //   F   Location = the adjustment reason, so the row is self-describing
    //                  (Location is free text, not formula-sensitive)
    //   I   Camp Win = 1 (same constant every other AttInput writer uses)
    //   J   DKP      = entry.Amount (the positive credit)
    //   K   Entry Tp = always "Misc Camp" -- a miscellaneous credit isn't a
    //                  camp/kill, so it's tagged with the catch-all value the
    //                  sheet's Tally/Main formulas recognize (NOT the
    //                  linkshell's attendance default, which may be blank or
    //                  another camp)
    public async Task AppendMiscDkpAsync(int dkpLedgerEntryId, CancellationToken cancellationToken)
    {
        var entry = await _db.DkpLedgerEntries
            .FirstOrDefaultAsync(e => e.Id == dkpLedgerEntryId, cancellationToken);
        if (entry is null)
        {
            _logger.LogDebug("AttInput misc skip: ledger entry {Id} not found.", dkpLedgerEntryId);
            return;
        }
        // Sign guard FIRST (before the idempotency stamp) so a deduction
        // never gets claimed here -- it belongs to the ManualPoints path.
        if (entry.Amount <= 0)
        {
            return;
        }
        if (entry.SheetAppendedAt.HasValue)
        {
            _logger.LogDebug("AttInput misc skip: ledger entry {Id} already appended.", dkpLedgerEntryId);
            return;
        }
        if (string.IsNullOrWhiteSpace(entry.CharacterName))
        {
            _logger.LogDebug("AttInput misc skip: ledger entry {Id} has no character name.", dkpLedgerEntryId);
            return;
        }

        var linkshell = await LoadConfiguredLinkshellAsync(entry.LinkshellId, cancellationToken);
        if (linkshell is null) return;

        // Always "Misc Camp" for a miscellaneous credit -- never the
        // linkshell's AttInputDefaultEntryType (that default is for
        // attendance snapshots / events and may be blank or another camp,
        // which left column K empty for these adjustment rows).
        var entryType = WindowEventEntryTypes.MiscCamp;

        var reason = new[] { entry.EditReason, entry.Details }
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))
            ?? "Manual DKP adjustment";

        // Colored label / separator row first -- same shape as the Window
        // Event header: only column C carries the label, every
        // formula-sensitive column stays blank, and the row is tinted after
        // append so the manual addition is visually separated.
        var separatorRow = new List<object>
        {
            string.Empty,            // A Player
            string.Empty,            // B Jobs
            reason,                  // C Label
            string.Empty,            // D Time
            string.Empty,            // E UTC offset
            string.Empty,            // F Location
            string.Empty,            // G (blank)
            string.Empty,            // H Player Name
            string.Empty,            // I Camp Window
            string.Empty,            // J DKP
            string.Empty,            // K Entry Type
        };

        var dataRow = BuildRow(
            playerName: entry.CharacterName,
            jobs: null,
            whenUtc: entry.OccurredAt,
            utcOffset: null,
            location: reason,
            campWindow: 1,
            dkp: entry.Amount,
            entryType: entryType);

        var appendResponse = await AppendRowsAsync(
            linkshell,
            new List<IList<object>> { separatorRow, dataRow },
            cancellationToken);
        if (TryGetFirstAppendedRow(appendResponse?.Updates?.UpdatedRange, out var labelRowNumber))
        {
            // The credit lives on the row directly under the label; that's
            // the row any later audit/locate path must target, so track it
            // (not the label row) as the entry's AttInput row.
            entry.AttInputRowNumber = labelRowNumber + 1;
            var tab = string.IsNullOrWhiteSpace(linkshell.AttInputTabName)
                ? DefaultTabName
                : linkshell.AttInputTabName!;
            await _sheets.FormatRowAsync(
                linkshell.Id,
                linkshell.GoogleSpreadsheetId!,
                tab,
                labelRowNumber,
                red: 1.0f,
                green: 0.93f,
                blue: 0.72f,
                cancellationToken);
        }

        entry.SheetAppendedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation(
            "AttInput misc append: ledger entry {Id} ({Amount} DKP, {Type}) -> 1 label + 1 row.",
            dkpLedgerEntryId, entry.Amount, entryType);
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

    private async Task<Google.Apis.Sheets.v4.Data.AppendValuesResponse> AppendRowsAsync(
        Linkshell linkshell,
        IList<IList<object>> rows,
        CancellationToken cancellationToken)
    {
        var tab = string.IsNullOrWhiteSpace(linkshell.AttInputTabName) ? DefaultTabName : linkshell.AttInputTabName!;
        var range = $"{tab}!A:K";
        return await _sheets.AppendAsync(linkshell.Id, linkshell.GoogleSpreadsheetId!, range, rows, cancellationToken);
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
        var time = whenUtc.ToString("h:mm:ss tt", CultureInfo.InvariantCulture);
        // Sheet rows historically use the "UTC-0500" shape. The addon's
        // os.date('%z') hands us a bare "-0500" with no prefix, so normalize
        // to always include the "UTC" tag instead of letting raw signed
        // offsets leak into column D and break the formula chain.
        string offsetTag;
        if (string.IsNullOrWhiteSpace(utcOffset))
        {
            offsetTag = "UTC+0000";
        }
        else if (utcOffset!.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
        {
            offsetTag = utcOffset;
        }
        else
        {
            offsetTag = "UTC" + utcOffset;
        }
        return new List<object>
        {
            playerName ?? string.Empty,            // A Player
            jobs ?? string.Empty,                  // B Jobs
            date,                                  // C Date REQ
            time,                                  // D Time
            offsetTag,                             // E UTC offset
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

    private static bool TryGetFirstAppendedRow(string? updatedRange, out int rowNumber)
    {
        rowNumber = 0;
        if (string.IsNullOrWhiteSpace(updatedRange)) return false;

        // Examples:
        //   AttInput!A11945:K11963
        //   'Att Input'!A11945:K11963
        var bangIndex = updatedRange.LastIndexOf('!');
        var rangePart = bangIndex >= 0 ? updatedRange[(bangIndex + 1)..] : updatedRange;
        var match = System.Text.RegularExpressions.Regex.Match(rangePart, @"^[A-Z]+(?<row>\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["row"].Value, out rowNumber);
    }
}
