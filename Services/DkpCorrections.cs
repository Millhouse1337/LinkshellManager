namespace LinkshellManagerDiscordApp.Services;

// Who changed a past event's DKP, and why (the reason is optional).
public sealed record DkpCorrectionNote(string? OfficerName, string? Reason)
{
    public static readonly DkpCorrectionNote None = new(null, null);
}

// A "Correction" ledger row: what an officer's change to a PAST event's DKP writes.
//
// The rows a past event paid are never edited or deleted afterwards. A change appends ONE
// correction row for the difference, pointing at the row it corrects (AuditRelatedLedgerEntryId)
// and filed under the same event, so the member's history shows the original payout, then who
// changed it, when, and why. The balance ends up exactly where an in-place edit would have put it.
//
// Corrections follow the same chain as DKP audit rows (DkpAuditChain), so Past Events
// (PastEventDkp), the next edit and Delete all count them with the row they correct.
public static class DkpCorrections
{
    public const string EntryType = "Correction";

    // Same limit as the DKP audit's reason.
    public const int MaxReasonLength = 500;

    // Float slack: a change smaller than this writes no row.
    public const double Epsilon = 1e-9;

    // "Correction by Alice: wrong rate. Past event DKP 10 -> 8."
    public static string Details(DkpCorrectionNote note, string change)
    {
        var officer = string.IsNullOrWhiteSpace(note.OfficerName) ? "an officer" : note.OfficerName.Trim();
        var reason = CleanReason(note.Reason);
        var head = reason is null ? $"Correction by {officer}" : $"Correction by {officer}: {reason}";
        if (!".!?".Contains(head[^1])) head += ".";
        return $"{head} {change}";
    }

    public static string? CleanReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

    public static string Amount(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

// Lifetime Total / Total Spent from ledger rows, with every correction folded into the row it
// corrects first. Without the fold, "earned 10, corrected by -4" would read as 10 earned and 4
// spent; folded it is 6 earned, the same figure the row would show had it been edited in place.
public static class DkpLifetimeTotals
{
    public readonly record struct Row(int Id, double Amount, string? EntryType, int? CorrectsId);

    // `counts` decides from the ROOT row (the one corrected) whether that row and its corrections
    // count; reconciliation rows (import seeds) are left out by the caller through it too.
    public static (double Earned, double Spent) Sum(IReadOnlyCollection<Row> rows, Func<Row, bool> counts)
    {
        var byId = new Dictionary<int, Row>();
        foreach (var row in rows) byId[row.Id] = row;

        var net = new Dictionary<int, double>();
        foreach (var row in rows)
        {
            var root = RootOf(row, byId);
            net[root.Id] = net.GetValueOrDefault(root.Id) + row.Amount;
        }

        double earned = 0, spent = 0;
        foreach (var (rootId, amount) in net)
        {
            if (!counts(byId[rootId])) continue;
            if (amount > 0) earned += amount;
            else if (amount < 0) spent += -amount;
        }
        return (earned, spent);
    }

    // A correction belongs to the row it corrects, when that row is in the set.
    private static Row RootOf(Row row, IReadOnlyDictionary<int, Row> byId)
    {
        var current = row;
        for (var depth = 0; depth < 50; depth++)
        {
            if (!string.Equals(current.EntryType, DkpCorrections.EntryType, StringComparison.Ordinal)
                || current.CorrectsId is not int parentId
                || !byId.TryGetValue(parentId, out var parent))
            {
                return current;
            }
            current = parent;
        }
        return current;
    }
}
