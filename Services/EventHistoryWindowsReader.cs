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
    IReadOnlyList<ArchivedWindowAttendee> Attendees);

// A closed event's whole window record. WindowCount is what "Window 3 of N" should read against.
public sealed record ArchivedWindowSet(int WindowCount, IReadOnlyList<ArchivedWindow> Windows)
{
    public static readonly ArchivedWindowSet Empty = new(0, Array.Empty<ArchivedWindow>());

    public bool HasWindows => Windows.Count > 0;

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

        if (rows.Count == 0)
        {
            return ArchivedWindowSet.Empty;
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
                    .ToList()))
            .ToList();

        return new ArchivedWindowSet(windowCount, windows);
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
