using System.Globalization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Services;

// Builds the attendance-snapshot sections (open events + unlinked snapshots) and the row mappers
// they share.
//
// This used to live entirely inside WindowEventsController, back when the snapshot pages were their
// own "Attendance System" nav group. Snapshots only ever come from HNM activity, so those sections
// now render on the Event System page instead — which means EventController.Index needs the same
// query and the same mapping WindowEventsController.History still uses. One implementation, two
// callers, rather than a copy that drifts.
public sealed class AttendanceSectionsBuilder
{
    // Cap on the unlinked-snapshot list. A linkshell that never triages them would otherwise grow
    // this page without bound.
    private const int MaxUnlinkedSnapshots = 100;

    private readonly ApplicationDbContext _db;

    public AttendanceSectionsBuilder(ApplicationDbContext db)
    {
        _db = db;
    }

    // The LIVE half of the attendance data: events still being captured, plus snapshots that
    // landed without a monster name.
    //
    // Ended events are excluded, whether they were closed by hand or handed off by an ended camp.
    // They are not live work -- an officer owes them a DKP post, not a scan -- and they now list
    // under Events Pending DKP Post (BuildClosedAsync with pendingDkpPostOnly). See
    // ApplyLiveFilter for why status alone was the wrong test.
    public async Task<WindowEventsViewModel> BuildAsync(
        int linkshellId,
        string? linkshellName,
        bool canManage,
        DateTimeZone userZone,
        CancellationToken cancellationToken)
    {
        var openEvents = await ApplyLiveFilter(
                _db.WindowEvents.AsNoTracking().Where(e => e.LinkshellId == linkshellId))
            .OrderByDescending(e => e.LastCapturedAtUtc)
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
            .Include(e => e.MemberDkpOverrides)
            .ToListAsync(cancellationToken);

        // How many there really are, so the section can say when it is hiding some. Every
        // /lsm now capture lands here now — filing is entirely manual — so the cap is reachable in
        // a way it was not when ingest auto-filed most posts.
        var unlinkedTotal = await _db.AttendanceSnapshots
            .AsNoTracking()
            .CountAsync(
                s => s.LinkshellId == linkshellId
                     && s.WindowEventId == null
                     && s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored,
                cancellationToken);

        // Newest first, then by alliance, so several alliances posting the same moment list as
        // 1, 2, 3 instead of in whatever order they reached the server.
        var unlinked = await _db.AttendanceSnapshots
            .AsNoTracking()
            .Where(s => s.LinkshellId == linkshellId
                        && s.WindowEventId == null
                        && s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored)
            .OrderByDescending(s => s.CapturedAtUtc)
            .ThenBy(s => s.AllianceNumber)
            .Take(MaxUnlinkedSnapshots)
            .Include(s => s.Entries)
            .ToListAsync(cancellationToken);

        // Linkshell roster character names, for the "Add a character by name…" typeahead on the
        // snapshot editor. Only fetched for managers (the only ones who can edit a snapshot roster).
        var rosterNames = canManage
            ? await _db.AppUserLinkshells
                .AsNoTracking()
                .Where(link => link.LinkshellId == linkshellId
                               && link.CharacterName != null
                               && link.CharacterName != "")
                .Select(link => link.CharacterName!)
                .Distinct()
                .OrderBy(name => name)
                .ToListAsync(cancellationToken)
            : new List<string>();

        return new WindowEventsViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshellName,
            CanManage = canManage,
            OpenEvents = openEvents.Select(e => MapWindowEvent(e, userZone)).ToList(),
            ClosedEvents = new(),
            UnlinkedSnapshots = unlinked.Select(s => MapSnapshot(s, userZone)).ToList(),
            UnlinkedTotalCount = unlinkedTotal,
            UnlinkedDisplayCap = MaxUnlinkedSnapshots,
            RosterCharacterNames = rosterNames,
        };
    }

    // WHERE AN ATTENDANCE EVENT LIVES, as shared predicates. Here for the same reason
    // ApplyClosedSearch is: the Event System page and the Activity each run their own copy of
    // these queries, and a card that drops out of one list has to drop into the other on BOTH
    // surfaces or it becomes invisible.
    //
    // The split is ENDED vs STILL LIVE -- deliberately not Open vs Closed. An HNM camp handed off
    // by HnmCampReviewHandoffService is Status=Open with CampEndedAtUtc set: a finished camp
    // waiting on review, not a live one. Keying on status alone filed every ended camp under
    // Current Field Activity, next to boards that were still being scanned.
    //
    // Live work: still being captured, so it belongs in Current Field Activity.
    public static IQueryable<WindowEvent> ApplyLiveFilter(IQueryable<WindowEvent> source)
        => source.Where(e => e.Status == WindowEventStatuses.Open && e.CampEndedAtUtc == null);

    // Ended, and nobody has posted the DKP yet -- the "Events Pending DKP Post" section.
    //
    // PostedToSheetAt is the gate, because it is the money: WindowEventDkpLedgerService sets it
    // when the ledger is written, and that is the moment the event stops being something an
    // officer owes work on. Until then it is deliberately kept OUT of Past Events
    // (EventController.BuildPastEventsAsync), so the past-event list means "settled" rather than
    // "over".
    //
    // Two ways in, because there are two ways to finish: a camp End Camp stamps CampEndedAtUtc,
    // and an officer closes a "/lsm now" event by hand.
    public static IQueryable<WindowEvent> ApplyPendingDkpPostFilter(IQueryable<WindowEvent> source)
        => source.Where(e => e.PostedToSheetAt == null
                             && (e.CampEndedAtUtc != null || e.Status == WindowEventStatuses.Closed));

    // The archive's search, as a composable filter over closed Window Events. Extracted so the
    // Event System page and the Discord Activity's own window-events endpoint run the SAME match
    // rules: both surfaces list the same cards, so a query that hits on one has to hit on the other.
    //
    // Blank/null means "no filter" and the source comes back untouched.
    public static IQueryable<WindowEvent> ApplyClosedSearch(IQueryable<WindowEvent> source, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return source;

        var pattern = $"%{query.Trim()}%";
        return source.Where(e =>
            (e.Name != null && EF.Functions.ILike(e.Name, pattern))
            || (e.CreatedByCharacterName != null && EF.Functions.ILike(e.CreatedByCharacterName, pattern))
            // Poster match spans EVERY snapshot, matching the in-memory filter this replaced.
            || e.Snapshots.Any(s =>
                s.CapturedByCharacterName != null && EF.Functions.ILike(s.CapturedByCharacterName, pattern))
            // Member match runs over the COMBINED roster, and BuildCombinedMembers builds that
            // from ACTIVE snapshots only — hence the status filter, so a search here can't match
            // a name the card doesn't list.
            || e.Snapshots.Any(s =>
                s.SnapshotStatus == AttendanceSnapshotStatuses.Active
                && s.Entries.Any(entry => EF.Functions.ILike(entry.CharacterName, pattern))));
    }

    // The CLOSED half: paged and searched. Same query, mapping and match rules the standalone
    // Attendance History page used to run inline (WindowEventsController.History), extracted so the
    // archive block on the Event System page and that page cannot disagree about what a search
    // matches.
    //
    // Paging happens in SQL, BEFORE the Includes. The inline version loaded every closed event with
    // its whole snapshot/entry tree and paged the result in memory, which was tolerable on a page
    // nobody visited and is not on /Event: each rendered card carries a combined-member table, one
    // table per snapshot, a dialog and two inline scripts.
    //
    // `pendingDkpPostOnly` picks which archive this is:
    //   true  -- the Event System page's "Events Pending DKP Post": ENDED events still owed a DKP
    //            post. This is the working queue, so it must not be diluted with settled events.
    //   false -- the standalone Attendance History page: EVERY closed event, posted or not.
    //            That page is the only place a posted-and-closed "/lsm now" event is still
    //            reachable -- those never get an EventHistory (WindowEventDkpLedgerService
    //            .ResolveCampEventHistory returns null without a camp), so they are not in Past
    //            Events either. Narrowing it too would make them unreachable.
    public async Task<WindowEventsHistoryViewModel> BuildClosedAsync(
        int linkshellId,
        string? linkshellName,
        bool canManage,
        DateTimeZone userZone,
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken,
        bool pendingDkpPostOnly = false)
    {
        pageSize = Math.Max(1, pageSize);
        var trimmedQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        var scoped = _db.WindowEvents.AsNoTracking().Where(e => e.LinkshellId == linkshellId);
        var baseQuery = pendingDkpPostOnly
            ? ApplyPendingDkpPostFilter(scoped)
            : scoped.Where(e => e.Status == WindowEventStatuses.Closed);

        baseQuery = ApplyClosedSearch(baseQuery, trimmedQuery);

        var totalCount = await baseQuery.CountAsync(cancellationToken);
        var pageNumber = Math.Clamp(
            page <= 0 ? 1 : page,
            1,
            Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize)));

        var closed = await baseQuery
            .OrderByDescending(e => e.LastCapturedAtUtc)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .Include(e => e.Snapshots).ThenInclude(s => s.Entries)
            .Include(e => e.MemberDkpOverrides)
            .ToListAsync(cancellationToken);

        return new WindowEventsHistoryViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshellName,
            CanManage = canManage,
            Query = trimmedQuery,
            Page = pageNumber,
            PageSize = pageSize,
            TotalCount = totalCount,
            Events = closed.Select(e => MapWindowEvent(e, userZone)).ToList(),
        };
    }

    public static WindowEventRow MapWindowEvent(WindowEvent item, DateTimeZone userZone)
    {
        // Pass the event itself: each snapshot needs its cadence (for "of 25") and its grid anchor
        // (to name the window of a capture taken before window numbering existed).
        var snapshots = item.Snapshots
            .OrderByDescending(s => s.CapturedAtUtc)
            .ThenBy(s => s.AllianceNumber)
            .Select(s => MapSnapshot(s, userZone, item))
            .ToList();
        var overrides = item.MemberDkpOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
            .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DkpAmount, StringComparer.OrdinalIgnoreCase);
        var combined = BuildCombinedMembers(item.Snapshots, overrides, item.DkpAmount, item.MiscDkpAmount);

        return new WindowEventRow
        {
            Id = item.Id,
            Name = item.Name,
            Status = item.Status,
            FirstCapturedAtUtc = item.FirstCapturedAtUtc,
            LastCapturedAtUtc = item.LastCapturedAtUtc,
            FirstCapturedDisplay = FormatPretty(item.FirstCapturedAtUtc, userZone),
            LastCapturedDisplay = FormatPretty(item.LastCapturedAtUtc, userZone),
            CreatedByCharacterName = item.CreatedByCharacterName,
            SnapshotCount = snapshots.Count,
            ActiveSnapshotCount = snapshots.Count(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active),
            IgnoredSnapshotCount = snapshots.Count(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Ignored),
            PendingSnapshotCount = snapshots.Count(s => s.IsPending),
            // From ACTIVE snapshots only, matching BuildCombinedMembers: the header count has to
            // describe the roster below it, and a pending alliance is not in that roster yet.
            AllianceNumbers = item.Snapshots
                .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active
                            && s.AllianceNumber.HasValue)
                .Select(s => s.AllianceNumber!.Value)
                .Distinct()
                .OrderBy(n => n)
                .ToList(),
            CombinedMemberCount = combined.Count,
            DkpAmount = item.DkpAmount,
            EntryType = item.EntryType,
            PostedToSheetAt = item.PostedToSheetAt,
            PostedToSheetDisplay = item.PostedToSheetAt.HasValue
                ? FormatPretty(item.PostedToSheetAt.Value, userZone)
                : null,
            Snapshots = snapshots,
            // Split once, here, so the two surfaces cannot disagree about what counts as Misc.
            WindowSnapshots = snapshots.Where(s => !s.IsMisc).ToList(),
            MiscSnapshots = snapshots.Where(s => s.IsMisc).ToList(),
            MiscSnapshotCount = snapshots.Count(s => s.IsMisc),
            MiscDkpAmount = item.MiscDkpAmount,
            WindowCount = WindowEventWindowGrid.WindowCount(item),
            HasWindowGrid = WindowEventWindowGrid.Minutes(item) > 0,
            CombinedMembers = combined,
        };
    }

    // `windowEvent` is the camp this snapshot belongs to; it supplies the cadence and the grid
    // anchor used to name the window. Omitted for an UNLINKED snapshot — one with no Window Event,
    // so no camp and no grid — which then shows no window at all.
    public static WindowSnapshotRow MapSnapshot(
        AttendanceSnapshot snapshot, DateTimeZone userZone, WindowEvent? windowEvent = null)
    {
        // Scanned names first, alphabetically — that block is the addon's evidence and stays intact.
        // Hand-added people go underneath in the order an officer entered them (by Id, not by name)
        // so a newly typed row appears where the eye already is: at the bottom, next to the input.
        var entries = snapshot.Entries
            .Where(e => !e.AddedManually)
            .OrderBy(e => e.CharacterName, StringComparer.OrdinalIgnoreCase)
            .Concat(snapshot.Entries.Where(e => e.AddedManually).OrderBy(e => e.Id))
            .Select(e => new AttendanceSnapshotEntryRow
            {
                Id = e.Id,
                CharacterName = e.CharacterName,
                MainJob = e.MainJob,
                MainJobLevel = e.MainJobLevel,
                SubJob = e.SubJob,
                SubJobLevel = e.SubJobLevel,
                Zone = e.Zone,
                AddedManually = e.AddedManually,
            })
            .ToList();

        // The camp's OWN grid (stamped at creation, HnmConfig as the fallback), not the monster's
        // built-in one — otherwise a linkshell that configured a different window count would see
        // its snapshots labelled "of 25" against a camp that ran 8.
        var gridWindows = windowEvent is null ? (int?)null : WindowEventWindowGrid.WindowCount(windowEvent);
        var hasGrid = windowEvent is not null && WindowEventWindowGrid.Minutes(windowEvent) > 0;

        // The STORED number wins: it was pinned when the capture was taken, against the grid as it
        // stood then. Snapshots posted before window numbering existed have none, so theirs is
        // derived here from the event's anchor — the same math, just applied late. Deriving is a
        // read-time fallback rather than a data backfill because the cadence table lives in
        // HnmConfig, and a SQL backfill would have to duplicate it and then drift from it.
        // A Misc capture has NO window, and the fallback below must not invent one for it.
        // Misc stores a null WindowNumber, which is exactly the shape that triggers the derivation
        // — so without this test a misc post on a gridded camp would render "Window 4 of 25"
        // sitting next to its own Misc chip.
        var isMisc = AttendanceSnapshotSlotKinds.IsMisc(snapshot.SlotKind);

        // A CAMP window carries its own name -- Open, Close, Kill -- stamped when the addon posted
        // it and copied onto the snapshot by HnmCampReviewHandoffService. Prefer it over the
        // positional "Window N", which is what the card showed for a camp whose tabs read Open /
        // Close / Kill in game: the same three posts, named one way in the addon and numbered here.
        //
        // Only a RECOGNISED label wins. Snapshot.Name is free text on a "/lsm now" capture, so
        // NormalizeWindowLabel is the gate: it returns null for anything that is not one of the
        // camp window names, and those fall through to the numbering exactly as before.
        var campWindowLabel = isMisc ? null : HnmConfig.NormalizeWindowLabel(snapshot.Name);
        var resolvedWindow = isMisc
            ? null
            : snapshot.WindowNumber
              ?? (windowEvent is not null
                  ? WindowEventWindowGrid.SnapshotWindowNumber(windowEvent, snapshot.CapturedAtUtc)
                  : null);

        return new WindowSnapshotRow
        {
            Id = snapshot.Id,
            WindowEventId = snapshot.WindowEventId,
            Name = snapshot.Name,
            SnapshotStatus = snapshot.SnapshotStatus,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            CapturedAtDisplay = FormatPretty(snapshot.CapturedAtUtc, userZone),
            CapturedByCharacterName = snapshot.CapturedByCharacterName,
            EntryCount = snapshot.EntryCount,
            AllianceNumber = snapshot.AllianceNumber,
            AllianceLabel = AttendanceSnapshotAlliances.Label(snapshot.AllianceNumber, snapshot.AllianceKey, snapshot.AllianceLeaderName),
            IsPending = snapshot.SnapshotStatus == AttendanceSnapshotStatuses.Pending,
            VerifiedAtDisplay = snapshot.VerifiedAtUtc.HasValue
                ? FormatPretty(snapshot.VerifiedAtUtc.Value, userZone)
                : null,
            WindowNumber = resolvedWindow,
            WindowLabel = campWindowLabel
                ?? (resolvedWindow is { } window
                    ? (hasGrid && gridWindows is { } total ? $"Window {window} of {total}" : $"Window {window}")
                    : null),
            SlotKind = AttendanceSnapshotSlotKinds.Resolve(snapshot.SlotKind),
            IsMisc = isMisc,
            SlotLabel = isMisc
                ? "Misc"
                : campWindowLabel
                    ?? (resolvedWindow is { } slotWindow
                        ? (hasGrid && gridWindows is { } slotTotal ? $"Window {slotWindow} of {slotTotal}" : $"Window {slotWindow}")
                        : null),
            PrimaryZone = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Zone))
                .GroupBy(e => e.Zone!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .FirstOrDefault(),
            Entries = entries,
        };
    }

    public static List<WindowCombinedMemberRow> BuildCombinedMembers(
        IEnumerable<AttendanceSnapshot> snapshots,
        IDictionary<string, double>? memberDkpOverrides = null,
        double? defaultDkpAmount = null,
        // Null means "misc pays what a window pays". Optional so the existing one-argument callers
        // (and their tests) keep compiling and keep their old behaviour exactly.
        double? miscDkpAmount = null)
    {
        return snapshots
            .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active)
            .SelectMany(s => s.Entries.Select(e => new { Snapshot = s, Entry = e }))
            .GroupBy(x => x.Entry.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.Snapshot.CapturedAtUtc).First().Entry;
                double? overrideAmount = null;
                if (memberDkpOverrides is not null && memberDkpOverrides.TryGetValue(g.Key, out var found))
                {
                    overrideAmount = found;
                }

                // A member seen in ANY window capture is an ordinary attendee, even if they also
                // turn up in a misc post. The misc rate is for the people who were ONLY ever there
                // off-window — the ones who stayed at a camp the shell never claimed.
                var sawWindow = g.Any(x => !AttendanceSnapshotSlotKinds.IsMisc(x.Snapshot.SlotKind));
                var sawMisc = g.Any(x => AttendanceSnapshotSlotKinds.IsMisc(x.Snapshot.SlotKind));
                var creditSource = sawWindow && sawMisc
                    ? "Both"
                    : sawMisc ? AttendanceSnapshotSlotKinds.Misc : AttendanceSnapshotSlotKinds.Window;
                var baseAmount = sawMisc && !sawWindow
                    ? miscDkpAmount ?? defaultDkpAmount
                    : defaultDkpAmount;
                return new WindowCombinedMemberRow
                {
                    CharacterName = g.Key,
                    MainJob = latest.MainJob,
                    MainJobLevel = latest.MainJobLevel,
                    SubJob = latest.SubJob,
                    SubJobLevel = latest.SubJobLevel,
                    Zone = latest.Zone,
                    SnapshotCount = g.Select(x => x.Snapshot.Id).Distinct().Count(),
                    AllianceNumbers = g
                        .Where(x => x.Snapshot.AllianceNumber.HasValue)
                        .Select(x => x.Snapshot.AllianceNumber!.Value)
                        .Distinct()
                        .OrderBy(n => n)
                        .ToList(),
                    DkpAmountOverride = overrideAmount,
                    EffectiveDkpAmount = overrideAmount ?? baseAmount,
                    CreditSource = creditSource,
                };
            })
            .ToList();
    }

    public static string FormatPretty(DateTime utc, DateTimeZone zone)
    {
        var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        var local = instant.InZone(zone);
        var localDateTime = local.ToDateTimeUnspecified();
        var zoneName = zone.GetZoneInterval(instant).Name;
        var day = localDateTime.Day;
        var suffix = (day % 100) is >= 11 and <= 13
            ? "th"
            : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        var month = localDateTime.ToString("MMMM", CultureInfo.InvariantCulture);
        var time = localDateTime.ToString("h:mm", CultureInfo.InvariantCulture);
        var meridian = localDateTime.ToString("tt", CultureInfo.InvariantCulture).ToLowerInvariant();
        return $"{month} {day}{suffix} {localDateTime.Year} {time}{meridian} {zoneName}";
    }
}
