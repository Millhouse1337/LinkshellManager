namespace LinkshellManagerDiscordApp.Services;

// Sharing one lump sum out evenly between several members.
//
// Gil is whole numbers, so an even split usually is not exactly even: 1,000,000 across three is
// 333,334 + 333,333 + 333,333. The leftover gil has to go SOMEWHERE, and it has to be the same
// somewhere every time — an entry whose halves do not sum to zero violates INV-2, and a Fix that
// reshuffles who got the extra gil would show a difference that is not really a difference.
//
// So the ordering lives in here rather than at the call sites. Recipients are sorted by name and
// the first (total % count) of them get one extra gil. Two callers cannot disagree about that,
// because there is only one place it is decided.
public static class TreasurySplit
{
    // Who gets what. The returned order IS the order the shares were allocated in, so a caller
    // rendering a preview and the writer building the lines agree without coordinating.
    public static IReadOnlyList<(TreasuryRecipient Recipient, long Share)> Allocate(
        long total, IReadOnlyList<TreasuryRecipient> recipients)
    {
        if (recipients.Count == 0)
        {
            return Array.Empty<(TreasuryRecipient, long)>();
        }

        // Direction is carried by the categories a kind names, never by the sign of an amount.
        var amount = Math.Abs(total);
        var ordered = Order(recipients);

        var baseShare = amount / ordered.Count;
        var remainder = (int)(amount % ordered.Count);

        var shares = new List<(TreasuryRecipient, long)>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            shares.Add((ordered[index], baseShare + (index < remainder ? 1 : 0)));
        }

        // The reason this class exists. If this ever fails the entry would not balance, and a
        // treasury that does not balance is worse than one that refuses to record.
        var allocated = shares.Sum(share => share.Item2);
        if (allocated != amount)
        {
            throw new InvalidOperationException(
                $"Split of {amount} across {ordered.Count} allocated {allocated}.");
        }

        return shares;
    }

    // A total order, so the same people in a different order always produce the same shares.
    // Name first because that is what the officer sees; the other two only break ties between
    // members who share a name, which unsynced characters legitimately can.
    private static List<TreasuryRecipient> Order(IReadOnlyList<TreasuryRecipient> recipients) =>
        recipients
            .Select((recipient, index) => (recipient, index))
            .OrderBy(entry => entry.recipient.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.recipient.AppUserId, StringComparer.Ordinal)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.recipient)
            .ToList();
}
