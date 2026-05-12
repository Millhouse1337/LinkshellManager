using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Centralises the linkshell-inventory side-effects of an auction item moving
// in or out of the "Received" state. Both the web app and the activity API
// flip auction-history statuses via different controllers; routing both
// through here keeps the inventory math consistent and idempotent.
public static class AuctionInventoryService
{
    public const string ReceivedStatus = "Received";

    // Apply the inventory delta implied by a status transition. Returns true
    // if a change was made so callers can decide whether to stamp UpdatedAt
    // on adjacent records or audit-log the action.
    //
    // Rules:
    //   * Transition INTO "Received" from any other status: decrement source
    //     item by 1, deleting the inventory row when Quantity reaches 0 to
    //     match the existing close-time behaviour.
    //   * Transition OUT OF "Received" to any other status: increment source
    //     item by 1 if the row still exists. If the row was previously
    //     deleted (Quantity hit 0 and was cleaned up) we cannot safely
    //     restore the original metadata, so we skip — undo is best-effort.
    //   * Anything without a SourceItemId, without a winner, or without a
    //     real transition is a no-op.
    public static async Task<bool> AdjustForStatusChangeAsync(
        ApplicationDbContext db,
        AuctionItem auctionItem,
        string? previousStatus,
        string? newStatus,
        int linkshellId,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (!auctionItem.SourceItemId.HasValue)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(auctionItem.CurrentHighestBidderAppUserId))
        {
            return false;
        }

        var wasReceived = IsReceived(previousStatus);
        var isReceived = IsReceived(newStatus);
        if (wasReceived == isReceived)
        {
            return false;
        }

        var inventory = await db.Items
            .FirstOrDefaultAsync(
                item => item.Id == auctionItem.SourceItemId.Value && item.LinkshellId == linkshellId,
                cancellationToken);

        if (isReceived)
        {
            if (inventory is null)
            {
                return false;
            }

            inventory.Quantity = Math.Max(0, inventory.Quantity - 1);
            inventory.UpdatedAt = occurredAtUtc;
            if (inventory.Quantity == 0)
            {
                db.Items.Remove(inventory);
            }
            return true;
        }

        if (inventory is null)
        {
            return false;
        }

        inventory.Quantity += 1;
        inventory.UpdatedAt = occurredAtUtc;
        return true;
    }

    private static bool IsReceived(string? status)
        => string.Equals(status, ReceivedStatus, StringComparison.OrdinalIgnoreCase);
}
