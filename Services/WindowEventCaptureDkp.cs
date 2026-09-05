using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// What a member is owed on a review row whose CAPTURES carry the money (WindowEvent.PerCaptureDkp):
// the sum of AttendanceSnapshotEntry.DkpAmount across every active capture they appear in.
//
// One home for the sum, because two very different readers have to agree to the penny — the card's
// roster column (AttendanceSectionsBuilder) and the ledger that actually credits it
// (WindowEventDkpLedgerService). They walk different shapes and would each have written their own
// loop; a per-character amount resolved in two places is precisely how the Captures column came to
// show a number the payout never used.
public static class WindowEventCaptureDkp
{
    // ACTIVE captures only, matching AttendanceSectionsBuilder.BuildCombinedMembers exactly. A
    // pending or ignored capture is not part of the roster, so its amounts must not be part of the
    // payout either — an officer rejecting a capture is rejecting what it pays.
    //
    // A null entry amount contributes nothing. That is the state of a person an officer ADDED
    // during review, and of every row written before captures were priced: neither is a claim that
    // they are owed the event baseline.
    public static Dictionary<string, double> SumByCharacter(IEnumerable<AttendanceSnapshot> snapshots)
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            if (snapshot.SnapshotStatus != AttendanceSnapshotStatuses.Active) continue;

            foreach (var entry in snapshot.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.CharacterName)) continue;
                var name = entry.CharacterName.Trim();
                totals[name] = totals.GetValueOrDefault(name) + (entry.DkpAmount ?? 0d);
            }
        }

        return totals;
    }
}
