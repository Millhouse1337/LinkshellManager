using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Reads a CLOSED event's archived attendance windows — the HNM half of a past event, which until
// the archive existed was deleted at close along with the Event row.
//
// Shared by both surfaces (the web EventHistory Details page and the Activity's Past events panel)
// so the two can't disagree about what a window is called or which of them closed the camp. It is
// deliberately a reader and nothing more: an archived window is a historical record, not something
// either surface edits.
//
// What it can NOT reconstruct, and why: HnmCampPricing.WindowValueFor prices a live window from
// the camp's own bonus overrides and the linkshell's defaults, and the camp Event carrying those
// is gone by the time anything here runs. So DkpAmount below is only ever the amount an officer
// EXPLICITLY set on that window. Re-deriving a bonus from today's linkshell settings would quote a
// number the event never actually paid, which is worse than quoting none.
//
// What it CAN recover is what each window PAID -- see Paid, and LoadPaidAsync for where it comes
// from. That is a record, not a re-derivation, so it is safe to show.
// What one window paid per person, low and high. Min == Max on the ordinary window, where everyone
// in it was paid the same; they differ only when an officer re-priced somebody during review, and
// then the spread is exactly what they need to see.
public sealed record DkpRange(double Min, double Max);

public sealed record ArchivedWindowAttendee(
    string CharacterName,
    // The member's roster main, set only when CharacterName is one of their alts — so a row can
    // render "Athmilk (alt of Edicius)" with no membership lookup. See AppUserEventWindow.
    string? MainCharacterName,
    string? Zone,
    DateTime VerifiedAt);

public sealed record ArchivedWindow(
    int Id,
    int SequenceNumber,
    // Already resolved for display: a stored label (normalized past the "On Time"/"Claim/Kill"
    // rename), else the camp's default naming, else "Window N".
    string Label,
    DateTime PostedAt,
    string? PostedBySource,
    double? DkpAmount,
    bool IsClosingWindow,
    bool IsKillWindow,
    IReadOnlyList<ArchivedWindowAttendee> Attendees,
    // What this window actually paid each person, off the camp's review card. Null when nothing
    // priced it -- see LoadPaidAsync.
    DkpRange? Paid = null);

// Who tagged the mob on this camp, read off its archived Claim Shield captures.
//
// Deliberately NOT an ArchivedWindow. A tag is not a roster read: its evidence is the addon
// watching an action land on the mob, so a tagger can be recorded having appeared in no window at
// all, and someone scanned in every window can have tagged nothing. Modelling it as a window would
// put it into "x of N", into the per-member window counts, and into the credit denominator the
// close path paid on — a display change that would read as a payout change.
//
// PostedAt is the FIRST lottery of the camp; a camp that lost several before winning one records
// each, and the taggers are unioned across them the same way the finalizer pays them once.
public sealed record ArchivedTagRoster(
    DateTime PostedAt,
    IReadOnlyList<ArchivedWindowAttendee> Taggers,
    // The tag bonus as it was paid, off the review card's Tag capture.
    DkpRange? Paid = null);

// A closed event's whole window record. WindowCount is what "Window 3 of N" should read against.
public sealed record ArchivedWindowSet(
    int WindowCount,
    IReadOnlyList<ArchivedWindow> Windows,
    // Null when nobody tagged, or when the camp predates the Claim Shield archive.
    ArchivedTagRoster? TagRoster = null)
{
    public static readonly ArchivedWindowSet Empty = new(0, Array.Empty<ArchivedWindow>());

    public bool HasWindows => Windows.Count > 0 || TagRoster is not null;

    // Distinct members seen across every window — the camp's real attendance, which can exceed the
    // history's participant list (an addon scan records people who never joined on the site).
    public int DistinctAttendeeCount => Windows
        .SelectMany(window => window.Attendees)
        .Select(attendee => attendee.CharacterName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
}

public static class EventHistoryWindowsReader
{
    public static async Task<ArchivedWindowSet> LoadAsync(
        ApplicationDbContext dbContext, EventHistory history, CancellationToken cancellationToken)
    {
        var rows = await dbContext.EventAttendanceWindows
            .AsNoTracking()
            .Include(window => window.Attendees)
            .Where(window => window.EventHistoryId == history.Id)
            .OrderBy(window => window.SequenceNumber)
            .ToListAsync(cancellationToken);

        var (paidByWindow, paidTags) = await LoadPaidAsync(dbContext, history.Id, cancellationToken);
        var tagRoster = await LoadTagRosterAsync(dbContext, history.Id, cancellationToken);
        if (tagRoster is not null && paidTags is not null)
        {
            tagRoster = tagRoster with { Paid = paidTags };
        }

        if (rows.Count == 0)
        {
            // A camp can still have tagged something it never posted a window for — the Claim
            // Shield fires off chat whether or not an officer read a roster.
            return tagRoster is null
                ? ArchivedWindowSet.Empty
                : new ArchivedWindowSet(0, Array.Empty<ArchivedWindow>(), tagRoster);
        }

        // The name-based count is the same CREDIT chain the close path used to pay this event, so
        // the denominator here matches the DKP that was actually awarded. Taking the max against
        // the highest posted sequence covers a camp that posted past its nominal count (a
        // WindowCountOverride the archive can't see, since the Event carrying it is gone).
        var windowCount = Math.Max(
            HnmConfig.GetWindowCount(history.EventName),
            rows.Max(window => window.SequenceNumber));

        var windows = rows
            .Select(window => new ArchivedWindow(
                window.Id,
                window.SequenceNumber,
                HnmConfig.NormalizeWindowLabel(window.Label)
                    ?? HnmConfig.GetDefaultWindowLabel(history.EventName, window.SequenceNumber, windowCount)
                    ?? $"Window {window.SequenceNumber}",
                window.PostedAt,
                window.PostedBySource,
                window.DkpAmount,
                window.IsClosingWindow,
                window.IsKillWindow,
                window.Attendees
                    .Where(attendee => !string.IsNullOrWhiteSpace(attendee.CharacterName))
                    .OrderBy(attendee => attendee.CharacterName, StringComparer.OrdinalIgnoreCase)
                    .Select(attendee => new ArchivedWindowAttendee(
                        attendee.CharacterName!.Trim(),
                        string.IsNullOrWhiteSpace(attendee.MainCharacterName) ? null : attendee.MainCharacterName.Trim(),
                        string.IsNullOrWhiteSpace(attendee.Zone) ? null : attendee.Zone.Trim(),
                        attendee.VerifiedAt))
                    .ToList(),
                paidByWindow.GetValueOrDefault(window.SequenceNumber)))
            .ToList();

        return new ArchivedWindowSet(windowCount, windows, tagRoster);
    }

    // What each window of this camp actually paid, read back off its review card.
    //
    // This is what makes a camp's own open / close / kill / tag amounts recoverable after all. The
    // board that carried the bonuses is recycled, but End Camp priced every capture on the review
    // card it staged -- one snapshot per posted window, keyed on the window's sequence -- and Post
    // pays exactly the sum of those. So the amounts come from there, including anything an officer
    // re-priced during review, rather than being re-derived from today's settings.
    //
    // Summed per character within a window before ranging. A member caught twice in one window has
    // a priced row and a zero row (the handoff pays a window once), and that is one payment of X,
    // not a spread of 0 to X.
    //
    // Only a card that prices its captures answers. A Manual Check In camp credits the check-in
    // range and writes no capture amounts, and a camp archived before capture pricing existed has
    // none either -- both come back empty, and the window shows no figure rather than a wrong one.
    private static async Task<(Dictionary<int, DkpRange> ByWindow, DkpRange? Tags)> LoadPaidAsync(
        ApplicationDbContext dbContext, int historyId, CancellationToken cancellationToken)
    {
        var cardId = await dbContext.WindowEvents
            .AsNoTracking()
            .Where(card => card.CampEventHistoryId == historyId && card.PerCaptureDkp)
            .Select(card => (int?)card.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (cardId is null)
        {
            return (new Dictionary<int, DkpRange>(), null);
        }

        var captures = await dbContext.AttendanceSnapshots
            .AsNoTracking()
            .Include(capture => capture.Entries)
            .Where(capture => capture.WindowEventId == cardId
                && capture.SnapshotStatus == AttendanceSnapshotStatuses.Active
                && capture.SlotKind != AttendanceSnapshotSlotKinds.Misc)
            .ToListAsync(cancellationToken);

        var byWindow = captures
            .Where(capture => capture.WindowNumber.HasValue)
            .GroupBy(capture => capture.WindowNumber!.Value)
            .Select(group => (Window: group.Key, Paid: RangeOf(group, includeZero: true)))
            .Where(row => row.Paid is not null)
            .ToDictionary(row => row.Window, row => row.Paid!);

        // The Tag capture also lists anyone the finalizer rostered that no window caught, at 0 --
        // they have to appear somewhere or Post would not pay them. Those rows are not what the
        // tag bonus paid, so the range is taken over the people it actually paid.
        var tags = RangeOf(
            captures.Where(capture => !capture.WindowNumber.HasValue
                && capture.Name == AttendanceSnapshotAlliances.ClaimShieldCaptureName),
            includeZero: false);

        return (byWindow, tags);
    }

    private static DkpRange? RangeOf(IEnumerable<AttendanceSnapshot> captures, bool includeZero)
    {
        var perCharacter = captures
            .SelectMany(capture => capture.Entries)
            .Where(entry => entry.DkpAmount.HasValue && !string.IsNullOrWhiteSpace(entry.CharacterName))
            .GroupBy(entry => entry.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Sum(entry => entry.DkpAmount!.Value))
            .Where(amount => includeZero || Math.Abs(amount) > 0.0001)
            .ToList();
        return perCharacter.Count == 0 ? null : new DkpRange(perCharacter.Min(), perCharacter.Max());
    }

    // The camp's taggers, unioned across every lottery it recorded.
    //
    // Every capture counts, won or lost: a tag is a tag whether or not the lottery went our way,
    // which is the same rule HnmStandardCampFinalizer pays the tag bonus on. Names are deduped
    // because tagging three lotteries is still one person who tagged.
    private static async Task<ArchivedTagRoster?> LoadTagRosterAsync(
        ApplicationDbContext dbContext, int historyId, CancellationToken cancellationToken)
    {
        var captures = await dbContext.ClaimShieldCaptures
            .AsNoTracking()
            .Include(capture => capture.Members)
            .Where(capture => capture.EventHistoryId == historyId)
            .OrderBy(capture => capture.CapturedAtUtc)
            .ToListAsync(cancellationToken);
        if (captures.Count == 0)
        {
            return null;
        }

        // Paired with their own capture's timestamp here rather than read back off
        // member.Capture: this query is AsNoTracking, so the inverse navigation is never fixed up
        // and every tagger would have come out stamped DateTime.MinValue.
        var taggers = captures
            .SelectMany(capture => capture.Members.Select(member => new { capture.CapturedAtUtc, member.CharacterName }))
            .Where(row => !string.IsNullOrWhiteSpace(row.CharacterName))
            .GroupBy(row => row.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            // Zone and main are not recorded on a tag — the Claim Shield reads a chat line, not a
            // party list — so those stay null rather than being invented from the roster. The time
            // is their FIRST tag of the camp.
            .Select(group => new ArchivedWindowAttendee(
                group.Key,
                null,
                null,
                group.Min(row => row.CapturedAtUtc)))
            .ToList();

        return taggers.Count == 0
            ? null
            : new ArchivedTagRoster(captures[0].CapturedAtUtc, taggers);
    }

    // How many windows each of the given closed events archived. For the Past events LIST, which
    // needs only "does this one have a window record, and how big" — loading every window and
    // every roster row for a page of events would be orders of magnitude more data than the list
    // itself. Events with no archived windows are simply absent from the result.
    public static async Task<Dictionary<int, int>> CountsByHistoryAsync(
        ApplicationDbContext dbContext, IReadOnlyCollection<int> historyIds, CancellationToken cancellationToken)
    {
        if (historyIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var counts = await dbContext.EventAttendanceWindows
            .AsNoTracking()
            .Where(window => window.EventHistoryId != null && historyIds.Contains(window.EventHistoryId.Value))
            .GroupBy(window => window.EventHistoryId!.Value)
            .Select(group => new { HistoryId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return counts.ToDictionary(row => row.HistoryId, row => row.Count);
    }
}
