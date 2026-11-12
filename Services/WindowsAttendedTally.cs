using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// How many windows each attendee of a windowed (Claim/Kill-style) event was credited for, for the
// two end-event paths (EventEndService.EndEventCoreAsync and ActivityDataController.EndEventAsync)
// so they cannot count differently.
//
// Counted per ACCOUNT, not per participation row. Both paths pay one row per account and skip the
// rest, so counting per row underpaid a member whose windows were credited on their OTHER row (a
// double-clicked sign-up made two). Since OneParticipationPerEvent an account has one row per event,
// but counting by account keeps the payout right whatever row a window hangs off. A window credited
// twice for one account counts once. Rows without an account (outside sign-ups) still count by row.
//
// Credits whose participation was removed (AppUserEventId null) are not counted, as before: these
// events never clear their roster, so such a credit belongs to someone an officer took off the event.
internal sealed class WindowsAttendedTally
{
    private readonly Dictionary<string, int> _byAppUserId;
    private readonly Dictionary<int, int> _byParticipationId;

    private WindowsAttendedTally(Dictionary<string, int> byAppUserId, Dictionary<int, int> byParticipationId)
    {
        _byAppUserId = byAppUserId;
        _byParticipationId = byParticipationId;
    }

    internal static readonly WindowsAttendedTally Empty = new(
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), new Dictionary<int, int>());

    internal int For(AppUserEvent participation) =>
        string.IsNullOrWhiteSpace(participation.AppUserId)
            ? _byParticipationId.GetValueOrDefault(participation.Id, 0)
            : _byAppUserId.GetValueOrDefault(participation.AppUserId, 0);

    internal static async Task<WindowsAttendedTally> LoadAsync(
        ApplicationDbContext db, int eventId, CancellationToken cancellationToken)
    {
        var credits = await db.AppUserEventWindows
            .Where(w => w.EventAttendanceWindow!.EventId == eventId && w.AppUserEventId != null)
            .Select(w => new
            {
                ParticipationId = w.AppUserEventId!.Value,
                AppUserId = w.AppUserEvent!.AppUserId,
                w.EventAttendanceWindowId,
            })
            .ToListAsync(cancellationToken);

        var byAppUserId = credits
            .Where(c => !string.IsNullOrWhiteSpace(c.AppUserId))
            .GroupBy(c => c.AppUserId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => c.EventAttendanceWindowId).Distinct().Count(),
                StringComparer.OrdinalIgnoreCase);
        var byParticipationId = credits
            .Where(c => string.IsNullOrWhiteSpace(c.AppUserId))
            .GroupBy(c => c.ParticipationId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.EventAttendanceWindowId).Distinct().Count());

        return new WindowsAttendedTally(byAppUserId, byParticipationId);
    }
}
