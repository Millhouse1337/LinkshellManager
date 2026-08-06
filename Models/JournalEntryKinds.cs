namespace LinkshellManagerDiscordApp.Models;

// WHY an entry exists. Orthogonal to WHAT it was, which is the category on its lines — the
// two axes being crammed into one RevenueEntry.EntryType column is the original defect.
public static class JournalEntryKinds
{
    // An ordinary record of gil moving.
    public const string Standard = "Standard";

    // The opposite of an earlier entry, cancelling it out. The original stays on the books.
    public const string Reversal = "Reversal";

    // The replacement half of a fix: a Reversal of the wrong entry plus a Correction with the
    // right numbers, recorded together so "Fix" is one action for the officer.
    public const string Correction = "Correction";

    // The gil a linkshell already had when it started using LSManager. Without this, an
    // adopting linkshell has to record its existing 40M as gil received and overstates
    // everything it ever took in, forever.
    public const string Opening = "Opening";

    // The difference between the books and the gil actually sitting on the mule, recorded
    // after someone logs in and counts it.
    public const string Adjustment = "Adjustment";

    // Built by ConvertRevenueEntriesToJournal from a legacy RevenueEntries row. Never written
    // by new code; kept so converted history is identifiable forever.
    public const string Migration = "Migration";

    // Kinds that must carry a reason, because each one asserts an earlier entry was wrong.
    public static bool RequiresReason(string? kind) =>
        string.Equals(kind, Reversal, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, Correction, StringComparison.OrdinalIgnoreCase);

    public static bool IsReversal(string? kind) =>
        string.Equals(kind, Reversal, StringComparison.OrdinalIgnoreCase);
}
