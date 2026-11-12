using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Every DKP audit correction of each given ledger row, keyed by that row's id.
//
// An audit row points at the row it corrects (AuditRelatedLedgerEntryId). The audit screen also
// lets an officer correct a correction, and that row points at the first audit rather than the
// original, so the chain is followed down and every link is filed under the ORIGINAL row.
//
// Followed by id, never by event: audits written before they copied the event id (2569028) carry
// no EventHistoryId, and they still belong to the row they correct.
//
// One copy shared by the Past Event edits (which reverse and re-amount these rows) and the Past
// Event display (which adds them up), so both always agree on what "this row's corrections" means.
public static class DkpAuditChain
{
    // Depth is capped only as a guard against a loop in bad data.
    private const int MaxDepth = 50;

    // `ledger` is the set to search: tracked when the caller will change the rows, AsNoTracking
    // when it only reads them.
    public static async Task<Dictionary<int, List<DkpLedgerEntry>>> ByCorrectedRowAsync(
        IQueryable<DkpLedgerEntry> ledger, IEnumerable<DkpLedgerEntry> rows, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, List<DkpLedgerEntry>>();
        var rootOf = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            result[row.Id] = new List<DkpLedgerEntry>();
            rootOf[row.Id] = row.Id;
        }

        var frontier = rootOf.Keys.ToList();
        for (var depth = 0; frontier.Count > 0 && depth < MaxDepth; depth++)
        {
            var parentIds = frontier;
            var children = await ledger
                .Where(e => e.AuditRelatedLedgerEntryId != null && parentIds.Contains(e.AuditRelatedLedgerEntryId.Value))
                .ToListAsync(cancellationToken);
            frontier = new List<int>();
            foreach (var child in children.OrderBy(c => c.Id))
            {
                if (rootOf.ContainsKey(child.Id)) continue;
                var root = rootOf[child.AuditRelatedLedgerEntryId!.Value];
                rootOf[child.Id] = root;
                result[root].Add(child);
                frontier.Add(child.Id);
            }
        }
        return result;
    }
}
