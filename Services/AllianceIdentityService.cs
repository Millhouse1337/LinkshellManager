using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Turns "who leads this alliance" into the alliance NUMBERS the rest of the app already speaks.
//
// WHY THIS EXISTS. The alliance number used to be typed by the poster (`/lsm alliance N`) and
// defaulted to 1. The FFXI client cannot see other alliances, so nothing could check the value —
// which meant a linkshell where nobody ran the command reported every alliance as 1 and the whole
// per-alliance feature silently collapsed into one row.
//
// The addon now reports an IDENTITY instead: the alliance leader's character name where the game
// confirms one (IParty:GetAllianceLeaderServerId), else the first poster's name. Two officers in the
// same alliance compute the same identity from their own clients without coordinating; two
// alliances compute different ones by construction. The number is assigned here, from that.
//
// The number is STORED rather than recomputed at read time on purpose: every existing query, chip,
// index and officer correction is number-based, and re-deriving on read would let a camp's history
// renumber itself the first time an earlier capture was deleted.
public sealed class AllianceIdentityService
{
    private readonly ApplicationDbContext _db;

    public AllianceIdentityService(ApplicationDbContext db)
    {
        _db = db;
    }

    // Normalizes an identity for comparison. Names arrive from party memory on several clients, and
    // only slot 0 is guaranteed to match the game's exact casing.
    public static string? NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    // The alliance number for `key` on this camp: the number an earlier capture with the same key
    // already got, or the next free one.
    //
    // Ceiling is AttendanceSnapshotAlliances.MaxAllianceNumber. A camp fielding a seventh distinct
    // alliance is past anything this app renders, so the overflow shares the last number rather
    // than inventing one the UI has no colour or column for.
    public async Task<int> ResolveNumberAsync(
        int windowEventId, string? allianceKey, CancellationToken cancellationToken)
    {
        var normalized = NormalizeKey(allianceKey);
        if (normalized is null)
        {
            return AttendanceSnapshotAlliances.Resolve(null);
        }

        var existing = await _db.AttendanceSnapshots
            .AsNoTracking()
            .Where(item => item.WindowEventId == windowEventId && item.AllianceKey != null)
            .Select(item => new { item.AllianceKey, item.AllianceNumber })
            .ToListAsync(cancellationToken);

        var match = existing.FirstOrDefault(item =>
            string.Equals(NormalizeKey(item.AllianceKey), normalized, StringComparison.Ordinal));
        if (match?.AllianceNumber is int already)
        {
            return already;
        }

        // Next free number = one past the highest already handed out on this camp. Counting DISTINCT
        // keys instead would reuse a number after a capture was deleted, quietly merging two
        // alliances in the display.
        var highest = existing
            .Where(item => item.AllianceNumber.HasValue)
            .Select(item => item.AllianceNumber!.Value)
            .DefaultIfEmpty(0)
            .Max();

        return AttendanceSnapshotAlliances.Resolve(highest + 1);
    }
}
