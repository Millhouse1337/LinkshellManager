using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Selling an item out of the linkshell's stash, and undoing it.
//
// Extracted because the old website and the Activity each had a slightly different copy of this,
// each with its own pair of unguarded saves.
public sealed class ItemSaleRecorder
{
    private readonly ApplicationDbContext _db;
    private readonly TreasuryJournalWriter _journal;

    public ItemSaleRecorder(ApplicationDbContext db, TreasuryJournalWriter journal)
    {
        _db = db;
        _journal = journal;
    }

    // Mark an item sold and record the gil. Idempotent: selling an already-sold item is a no-op.
    //
    // The item's Sold flags and the treasury entry land in ONE transaction. Both paths previously did
    // two unguarded saves, so a failure between them left the treasury recording a sale the item did
    // not — and the balance was permanently wrong by the sale price, with nothing to point at.
    // soldBy is WHO SOLD IT, which is not the same person as the actor: an officer records sales
    // other members made all the time, and this used to store the clicker as the seller. It is also
    // the answer to where the gil went — whoever sold it is holding it until they hand it on — so
    // one answer fills both the item's seller and the treasury entry's holder. Callers require it;
    // it stays nullable here so the writer can still record an entry that genuinely has no answer.
    public async Task RecordSaleAsync(
        Item item,
        long salePrice,
        TreasuryActor actor,
        TreasuryHolder? soldBy,
        CancellationToken cancellationToken)
    {
        if (item.IsSold)
        {
            return;
        }
        // Both callers already clamp, but a negative sale price must never reach the journal: the
        // amount is a magnitude and the direction belongs to the categories.
        if (salePrice < 0)
        {
            salePrice = 0;
        }

        var now = DateTime.UtcNow;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var entry = await _journal.RecordAsync(
            item.LinkshellId,
            new TreasuryEntryRequest(
                TreasuryTransactionKinds.SoldAnItem,
                salePrice,
                now,
                Memo: item.Quantity > 1 ? $"Sold: {item.ItemName} (x{item.Quantity})" : $"Sold: {item.ItemName}",
                HolderAppUserId: soldBy?.AppUserId,
                HolderCharacterName: soldBy?.CharacterName,
                Source: JournalEntrySources.ItemSale,
                SourceItemId: item.Id),
            actor,
            cancellationToken);

        item.IsSold = true;
        item.SoldPrice = salePrice;
        item.SoldAt = now;
        // The seller, not the recorder. Falling back to the actor keeps an older caller honest
        // rather than blank, but every live caller now asks.
        item.SoldByCharacterName = string.IsNullOrWhiteSpace(soldBy?.CharacterName)
            ? actor.CharacterName
            : soldBy!.CharacterName;
        item.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        // Only knowable after the save, hence the second one — but inside the same transaction.
        item.JournalEntryId = entry.Id;
        await _db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    // Undo a sale. The treasury entry is REVERSED, not deleted: the sale happened and then was undone,
    // and that is a different fact from "it never happened", which is all a delete could say. The
    // original stays in the transaction list chipped "Reversed", with the reversal beside it.
    //
    // Idempotent like RecordSaleAsync: undoing an item that is not sold is a no-op. A second undo (a
    // double click, a stale page) used to reach the fallback lookup below with no stored link, find
    // the first undo's REVERSAL (it names the same item as its source) and reverse that, putting the
    // gil back on the books for an unsold item.
    public async Task ReverseSaleAsync(Item item, TreasuryActor actor, CancellationToken cancellationToken)
    {
        if (!item.IsSold)
        {
            return;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // The entry whose gil is on the books now: the sale, or the Correction of the last Fix to it.
        // Null when an officer already reversed it from the treasury list -- the gil is already off
        // the books then, so only the item goes back to unsold.
        var entry = await FindLiveSaleEntryAsync(item, cancellationToken);
        if (entry is not null)
        {
            await _journal.ReverseAsync(
                entry,
                $"Sale of {item.ItemName} was undone.",
                actor,
                cancellationToken);
        }

        item.IsSold = false;
        item.SoldPrice = null;
        item.SoldAt = null;
        item.SoldByCharacterName = null;
        item.JournalEntryId = null;
        // Cleared for the old binary's benefit too, for the one release both columns are live.
        item.RevenueEntryId = null;
        item.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // A Fix from the treasury list reverses the sale entry and records a Correction pointing at it with
    // the right amount, so after a Fix the gil on the books is the Correction's. Follow those links to
    // the entry nothing has reversed yet; null when the chain ends in a plain reversal.
    private async Task<JournalEntry?> FindLiveSaleEntryAsync(Item item, CancellationToken cancellationToken)
    {
        var entry = await FindSaleEntryAsync(item, cancellationToken);
        // Bounded: each step is a Fix someone made by hand, so a long chain means bad data, not a sale.
        for (var steps = 0; entry is not null && await _journal.HasReversalAsync(entry, cancellationToken); steps++)
        {
            if (steps >= 50)
            {
                return null;
            }
            var reversedId = entry.Id;
            entry = await _db.JournalEntries
                .Include(candidate => candidate.Lines)
                .Where(candidate => candidate.ReversesJournalEntryId == reversedId
                    && candidate.Kind == JournalEntryKinds.Correction
                    && candidate.Status == JournalEntryStatuses.Confirmed)
                .OrderByDescending(candidate => candidate.Sequence)
                .FirstOrDefaultAsync(cancellationToken);
        }
        return entry;
    }

    // Prefer the stored link. Fall back to the entry that names this item as its source, which covers
    // items sold before the column existed and items whose history came through the conversion.
    private async Task<JournalEntry?> FindSaleEntryAsync(Item item, CancellationToken cancellationToken)
    {
        if (item.JournalEntryId.HasValue)
        {
            var byId = await _db.JournalEntries
                .Include(entry => entry.Lines)
                .FirstOrDefaultAsync(entry => entry.Id == item.JournalEntryId.Value, cancellationToken);
            if (byId is not null)
            {
                return byId;
            }
        }

        return await _db.JournalEntries
            .Include(entry => entry.Lines)
            .Where(entry => entry.SourceItemId == item.Id
                && entry.Source == JournalEntrySources.ItemSale
                && entry.Status == JournalEntryStatuses.Confirmed
                // Never a reversal: it copies the sale's source item, and reversing it puts the gil back.
                && entry.Kind != JournalEntryKinds.Reversal
                // Not one that has already been reversed, or undoing twice would double-count.
                && !_db.JournalEntries.Any(other => other.ReversesJournalEntryId == entry.Id))
            .OrderByDescending(entry => entry.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
