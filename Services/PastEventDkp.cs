using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// What each attendee really got from a past event, read from the DKP LEDGER -- the one record of
// money -- rather than from AppUserEventHistory.EventDkp.
//
// WHY. EventDkp is a copy of the payout, written at close and then patched by every path that
// later moves the money (Past Event edits, a review card's Update, the DKP audit). Any path that
// forgot left Past Events quoting an old amount: an audit written before audits moved the figure,
// an audit of a camp payout, an officer's "Add" on a camp. The ledger rows are what the balance
// was built from, so adding them up can never disagree with it.
//
// A member's figure for an event = their earned row(s) filed under it ("EventEarned" on a timed
// event, "SnapshotEarned" on a camp, including an audit "Add") + every DKP audit correction of
// those rows (DkpAuditChain, which also finds old audits filed under no event). Loot rows filed
// under the event are spending, not earnings, and are left out, and so are audits of them.
//
// An attendee with no such rows keeps their stored EventDkp: a name with no account (nothing to
// pay), a member who had left when the event closed, a Loot Council event that paid nothing.
public static class PastEventDkp
{
    // The rows that ARE a member's earnings from an event.
    public static readonly IReadOnlyList<string> EarnedEntryTypes = new[] { "EventEarned", "SnapshotEarned" };

    public static async Task<PastEventDkpTotals> LoadAsync(
        ApplicationDbContext db, IReadOnlyCollection<int> historyIds, CancellationToken cancellationToken)
    {
        if (historyIds.Count == 0) return PastEventDkpTotals.Empty;

        var ids = historyIds.Distinct().ToList();
        var earnedTypes = EarnedEntryTypes.ToList();
        var earnedRows = await db.DkpLedgerEntries
            .AsNoTracking()
            .Where(e => e.EventHistoryId != null && ids.Contains(e.EventHistoryId.Value)
                        && e.AppUserId != null
                        && earnedTypes.Contains(e.EntryType))
            .ToListAsync(cancellationToken);
        var auditsByRow = await DkpAuditChain.ByCorrectedRowAsync(
            db.DkpLedgerEntries.AsNoTracking(), earnedRows, cancellationToken);

        // Filed under the earned row's event and account: a correction belongs to what it corrects.
        var totals = new Dictionary<(int HistoryId, string AppUserId), double>(KeyComparer.Instance);
        foreach (var row in earnedRows)
        {
            var key = (row.EventHistoryId!.Value, row.AppUserId!);
            var amount = row.Amount + auditsByRow[row.Id].Sum(a => a.Amount);
            totals[key] = totals.GetValueOrDefault(key) + amount;
        }
        return new PastEventDkpTotals(totals);
    }

    private sealed class KeyComparer : IEqualityComparer<(int HistoryId, string AppUserId)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((int HistoryId, string AppUserId) x, (int HistoryId, string AppUserId) y) =>
            x.HistoryId == y.HistoryId && StringComparer.OrdinalIgnoreCase.Equals(x.AppUserId, y.AppUserId);

        public int GetHashCode((int HistoryId, string AppUserId) key) =>
            HashCode.Combine(key.HistoryId, StringComparer.OrdinalIgnoreCase.GetHashCode(key.AppUserId));
    }
}

public sealed class PastEventDkpTotals
{
    public static readonly PastEventDkpTotals Empty = new(new Dictionary<(int, string), double>());

    private readonly IReadOnlyDictionary<(int HistoryId, string AppUserId), double> _totals;

    internal PastEventDkpTotals(IReadOnlyDictionary<(int HistoryId, string AppUserId), double> totals) =>
        _totals = totals;

    // The figure to show for one attendee: their ledger total for the event, or the stored EventDkp
    // when the ledger holds nothing for them there (see PastEventDkp).
    public double? For(AppUserEventHistory attendee)
    {
        if (!string.IsNullOrWhiteSpace(attendee.AppUserId)
            && _totals.TryGetValue((attendee.EventHistoryId, attendee.AppUserId), out var total))
        {
            return total;
        }
        return attendee.EventDkp;
    }

    // The event's total: what its attendees are shown, added up.
    public double TotalFor(IEnumerable<AppUserEventHistory> attendees) => attendees.Sum(a => For(a) ?? 0);
}
