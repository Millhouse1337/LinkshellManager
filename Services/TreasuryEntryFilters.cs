using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// The transactions list's chips, as one predicate per chip.
//
// ONE copy, because the web and the Activity both render this row and a chip that means different
// things on the two surfaces is worse than no chip at all. It lived twice — once in each controller
// — and had already drifted.
//
// Direction comes from whether GIL ON HAND moved, never from matching a string on a type column.
// String matching is what used to file one entry under both chips at once.
public static class TreasuryEntryFilters
{
    public const string GilIn = "in";
    public const string GilOut = "out";
    public const string Fixed = "fixed";
    public const string Reversed = "reversed";

    public static IQueryable<JournalEntry> Apply(IQueryable<JournalEntry> query, string? filter) =>
        filter?.Trim().ToLowerInvariant() switch
        {
            GilIn => query.Where(entry => entry.Lines.Any(line =>
                line.AccountNumber == TreasuryAccounts.GilOnHand && line.Amount > 0)),
            GilOut => query.Where(entry => entry.Lines.Any(line =>
                line.AccountNumber == TreasuryAccounts.GilOnHand && line.Amount < 0)),

            // A fix is ONE action that records two entries: the replacement carrying the right
            // numbers, and the reversal that cancelled the wrong ones. This chip is the pair worth
            // reading — what it said and what it says now — so the mechanical cancel in between is
            // left out. It would only ever be the same figures with the sign flipped.
            Fixed => query.Where(entry =>
                entry.Kind == JournalEntryKinds.Correction
                || query.Any(other => other.Kind == JournalEntryKinds.Correction
                    && other.ReversesJournalEntryId == entry.Id)),

            // Cancelled outright, and ONLY that. The exclusion is what makes this chip worth having:
            // without it every typo anyone ever corrected lands here too and buries the handful of
            // entries that were genuinely called off — which is the whole reason Fixed exists.
            //
            // Two things belong to a fix rather than here: the entry a Correction replaced, and the
            // reversal half recorded alongside that Correction (same original, so the same
            // ReversesJournalEntryId). The second test is guarded on Kind == Reversal so a
            // Correction that was itself later reversed still shows up — it was reversed outright.
            Reversed => query.Where(entry =>
                (entry.Kind == JournalEntryKinds.Reversal
                    || query.Any(other => other.ReversesJournalEntryId == entry.Id))
                && !query.Any(other => other.Kind == JournalEntryKinds.Correction
                    && (other.ReversesJournalEntryId == entry.Id
                        || (entry.Kind == JournalEntryKinds.Reversal
                            && other.ReversesJournalEntryId == entry.ReversesJournalEntryId)))),

            _ => query,
        };
}
