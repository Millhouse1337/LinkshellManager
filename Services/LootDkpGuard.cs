using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Guards loot awards against a winner's DKP balance: you can't be assigned event
// loot you can't afford. Event loot is deducted in a batch at event close, so the
// check has to run when the loot is RECORDED (otherwise the balance only goes
// negative later, with nothing to stop it). "Available" = current DKP minus
// auction-locked DKP (AuctionDkpService) minus loot already pending for the same
// winner in the same event (since those costs aren't deducted until close either).
// Only DKP/Hybrid structures spend DKP; LootCouncil is skipped. A free-text or
// non-roster winner can't be balance-checked, so it's allowed through (the winner
// is separately required to be a roster member by the callers).
public static class LootDkpGuard
{
    // The member a typed winner name belongs to, or null: the shared CharacterResolver rule, so the
    // guard, the close, End Camp, hand-entered loot and loot edits all agree on who a name is.
    // Tracked, because the live-card Add Loot path goes on to record this member's account.
    public static async Task<AppUserLinkshell?> ResolveWinnerAsync(
        ApplicationDbContext db, int linkshellId, string? winnerName, CancellationToken cancellationToken)
    {
        if (CharacterResolver.Normalize(winnerName) is null)
        {
            return null;
        }

        var roster = await CharacterResolver.LoadMembershipsAsync(db, linkshellId, cancellationToken);
        return roster.Resolve(winnerName);
    }

    // Returns null when the award is allowed, or a human-readable error to surface.
    public static async Task<string?> CheckEventLootAsync(
        ApplicationDbContext db,
        DkpPoolResolver dkpPools,
        DkpPoolBalanceService dkpPoolBalances,
        int eventId,
        int linkshellId,
        string? winnerName,
        double cost,
        CancellationToken cancellationToken)
    {
        if (cost <= 0 || string.IsNullOrWhiteSpace(winnerName))
        {
            return null;
        }

        var lootStructure = await db.Linkshells
            .Where(l => l.Id == linkshellId)
            .Select(l => l.LootStructure)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.Equals(lootStructure, "LootCouncil", StringComparison.OrdinalIgnoreCase))
        {
            return null; // No DKP economy — nothing to overdraw.
        }

        var name = winnerName.Trim();

        var member = await ResolveWinnerAsync(db, linkshellId, name, cancellationToken);
        if (member?.AppUserId is null)
        {
            return null; // Unknown / free-text winner — can't balance-check.
        }

        // This event's loot is bought with the pool this event's TYPE earns into. That's the whole
        // point of the feature: on a Sky event, a member with Sky+Sea+Dynamis pooled together can
        // spend all of it, and a member with DKP only in some unrelated pool can spend none of it.
        var eventType = await db.Events
            .Where(e => e.Id == eventId)
            .Select(e => e.EventType)
            .FirstOrDefaultAsync(cancellationToken);
        var map = await dkpPools.GetMapAsync(linkshellId, cancellationToken);
        var poolId = map.Resolve(eventType);

        // ComputePoolAvailableDkpAsync already subtracts the DKP this member has committed to loot
        // in still-live events of the same pool that hasn't been deducted yet, so two items in one
        // event can't each pass and together overdraw — no separate per-event subtraction is needed
        // here (doing so would double-count).
        var available = await AuctionDkpService.ComputePoolAvailableDkpAsync(
            db, dkpPoolBalances, member.AppUserId, linkshellId, poolId, cancellationToken);

        // Under Hybrid the number typed is a PERCENT of the winner's pool balance, not DKP, so it is
        // turned into the DKP the close will actually charge before comparing -- the same sum
        // ManualLootService does. Comparing the raw percent refused 50% for a member with 40 DKP.
        if (string.Equals(lootStructure, "Hybrid", StringComparison.OrdinalIgnoreCase))
        {
            var pct = Math.Clamp(cost, 0, 100);
            var increment = await db.Linkshells
                .Where(l => l.Id == linkshellId)
                .Select(l => l.DkpRoundingIncrement)
                .FirstOrDefaultAsync(cancellationToken);
            var balance = Math.Max(0, await dkpPoolBalances.GetCommittedAsync(
                linkshellId, member.AppUserId, poolId, cancellationToken));
            cost = LootDkpCalculator.ComputeHybridDebit(balance, pct, DkpRounding.StepFor(increment));
        }

        if (cost > available + 0.0001)
        {
            // Name the pool only when the linkshell actually has more than one — otherwise the
            // message is exactly the one officers have always seen.
            var poolLabel = map.HasMultiplePools ? $" {map.NameFor(poolId)}" : string.Empty;
            return $"{name} only has {available:0.##}{poolLabel} DKP available — not enough for this item ({cost:0.##} DKP).";
        }

        return null;
    }
}
