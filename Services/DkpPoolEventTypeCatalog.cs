using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// One assignable event type on the DKP Pools card.
//   Key         - the canonical string stored in DkpPoolEventType.EventType
//   EarnedTotal - lifetime DKP earned under this event type, so an officer can see what a
//                 remap actually moves before they commit to it
//   IsCustom    - not in the built-in vocabulary; it came out of this linkshell's own data
public sealed record DkpPoolEventTypeOption(string Key, bool IsCustom, double EarnedTotal, bool InUse);

// Everything a linkshell can assign to a pool: the built-in content event types (Sky, Sea, HNM …)
// plus any custom string this linkshell actually used — the web event form has an "Other"
// free-text field, so "Sky Farm" is a real possibility and has to be assignable rather than
// silently falling into the default pool forever.
//
// The window-event camp tags (Kings Camp, Wyrms Camp, Misc Camp, Kill) are DELIBERATELY excluded:
// they're attendance camp labels, not content event types, and officers don't group DKP by them.
// Window-event ledger rows are still stamped with them, but since they're never assignable they
// just fall through to the default pool like any unmapped type.
public sealed class DkpPoolEventTypeCatalog
{
    // The pseudo event type DkpImportService stamps on its reconciliation rows.
    private const string ImportEventType = "Sheet";

    // Window-event camp tags aren't content event types, so they never appear on the DKP grouping
    // card — not as built-ins, and not resurrected as "custom" types from the ledger/event data.
    private static readonly HashSet<string> ExcludedTypes = new(
        WindowEventEntryTypes.All.Select(DkpPoolEventType.Normalize), StringComparer.Ordinal);

    private readonly ApplicationDbContext _db;

    public DkpPoolEventTypeCatalog(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<DkpPoolEventTypeOption>> LoadAsync(int linkshellId, CancellationToken cancellationToken)
    {
        // Lifetime EARNED per event type (positive ledger amounts only). Doubles as the "what
        // does this remap move" hint and as the source of the unmapped-types warning.
        //
        // "Sheet" is excluded: it's the pseudo-type on DKP-import reconciliation rows, not
        // something anyone ran, and offering it as assignable would be nonsense (those rows are
        // pinned to the default pool anyway).
        var earnedByType = await _db.DkpLedgerEntries
            .Where(entry => entry.LinkshellId == linkshellId
                && entry.EventType != null
                && entry.EventType != ImportEventType
                && entry.Amount > 0)
            .GroupBy(entry => entry.EventType!)
            .Select(group => new { EventType = group.Key, Earned = group.Sum(entry => entry.Amount) })
            .ToListAsync(cancellationToken);

        var fromEvents = await _db.Events
            .Where(item => item.LinkshellId == linkshellId && item.EventType != null)
            .Select(item => item.EventType!)
            .Distinct()
            .ToListAsync(cancellationToken);

        var fromHistory = await _db.EventHistories
            .Where(item => item.LinkshellId == linkshellId && item.EventType != null)
            .Select(item => item.EventType!)
            .Distinct()
            .ToListAsync(cancellationToken);

        var builtIn = EventTypeVocabulary.All.ToList();

        // Canonical casing: a built-in spelling always wins over whatever casing happens to be
        // sitting in the data.
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
        void Register(string? raw, bool isBuiltIn)
        {
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value) || value.Length > 256)
            {
                // Events.EventType is unbounded text; the ledger caps at 256. Anything longer
                // could never round-trip, so it isn't assignable.
                return;
            }
            var key = DkpPoolEventType.Normalize(value);
            if (ExcludedTypes.Contains(key))
            {
                // Window-event camp tags (Kings Camp, Wyrms Camp, Misc Camp, Kill) aren't event
                // types here, even when they've earned DKP in the ledger.
                return;
            }
            if (isBuiltIn || !canonical.ContainsKey(key))
            {
                canonical[key] = value;
            }
        }

        foreach (var value in builtIn) { Register(value, isBuiltIn: true); }
        foreach (var value in fromEvents) { Register(value, isBuiltIn: false); }
        foreach (var value in fromHistory) { Register(value, isBuiltIn: false); }
        foreach (var row in earnedByType) { Register(row.EventType, isBuiltIn: false); }

        var builtInKeys = new HashSet<string>(builtIn.Select(DkpPoolEventType.Normalize), StringComparer.Ordinal);
        var earnedByKey = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in earnedByType)
        {
            var key = DkpPoolEventType.Normalize(row.EventType);
            earnedByKey[key] = earnedByKey.GetValueOrDefault(key) + row.Earned;
        }
        var inUseKeys = new HashSet<string>(
            fromEvents.Concat(fromHistory).Select(DkpPoolEventType.Normalize), StringComparer.Ordinal);

        return canonical
            .Select(pair => new DkpPoolEventTypeOption(
                pair.Value,
                IsCustom: !builtInKeys.Contains(pair.Key),
                EarnedTotal: earnedByKey.GetValueOrDefault(pair.Key),
                InUse: inUseKeys.Contains(pair.Key) || earnedByKey.ContainsKey(pair.Key)))
            // Built-ins first (in vocabulary order), then customs alphabetically.
            .OrderBy(option => option.IsCustom)
            .ThenBy(option => option.IsCustom
                ? int.MaxValue
                : builtIn.FindIndex(value => string.Equals(value, option.Key, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
