using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Selling an item out of the linkshell's stash, and undoing it.
//
// Extracted because both surfaces do this and both used to do it slightly differently — the same
// twenty lines copied into ManageItemController and ActivityDataController, each with its own pair of
// unguarded saves. One function means the web and Discord cannot drift, which is the actual fix for
// two front-ends disagreeing about the same table.
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
    public async Task RecordSaleAsync(Item item, long salePrice, TreasuryActor actor, CancellationToken cancellationToken)
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
                Source: JournalEntrySources.ItemSale,
                SourceItemId: item.Id),
            actor,
            cancellationToken);

        item.IsSold = true;
        item.SoldPrice = salePrice;
        item.SoldAt = now;
        item.SoldByCharacterName = actor.CharacterName;
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
    public async Task ReverseSaleAsync(Item item, TreasuryActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var entry = await FindSaleEntryAsync(item, cancellationToken);
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
                // Not one that has already been reversed, or undoing twice would double-count.
                && !_db.JournalEntries.Any(other => other.ReversesJournalEntryId == entry.Id))
            .OrderByDescending(entry => entry.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
