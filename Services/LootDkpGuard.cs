using LinkshellManagerDiscordApp.Data;
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

        // Resolve the winner (main OR alt) to a member, case-insensitively. Loaded
        // into memory so the comparison isn't at the mercy of the DB collation.
        var members = await db.AppUserLinkshells
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == linkshellId && m.AppUserId != null)
            .ToListAsync(cancellationToken);

        var member = members.FirstOrDefault(m =>
            string.Equals(m.CharacterName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.AppUser?.AltCharacterName1, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.AppUser?.AltCharacterName2, name, StringComparison.OrdinalIgnoreCase));
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
