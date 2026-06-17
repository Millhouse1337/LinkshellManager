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

        var available = await AuctionDkpService.ComputeAvailableDkpAsync(
            db, member.AppUserId, linkshellId, cancellationToken);

        // Subtract loot already awarded to this winner in this event but not yet
        // deducted (deduction happens at close), so two items in one event can't
        // each pass the check and together overdraw the member. Match ALL of the
        // member's characters (main + both alts) so loot won on an alt and loot won
        // on the main both count against the one shared account balance.
        var memberNames = new[] { member.CharacterName, member.AppUser?.AltCharacterName1, member.AppUser?.AltCharacterName2 }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = await db.EventLootDetails
            .Where(d => d.EventId == eventId
                        && d.ItemWinner != null
                        && d.WinningDkpSpent != null)
            .Select(d => new { d.ItemWinner, d.WinningDkpSpent })
            .ToListAsync(cancellationToken);
        var pendingForWinner = pending
            .Where(d => d.ItemWinner != null && memberNames.Contains(d.ItemWinner.Trim()))
            .Sum(d => (double)d.WinningDkpSpent!.Value);

        var effective = available - pendingForWinner;
        if (cost > effective + 0.0001)
        {
            return $"{name} only has {effective:0.##} DKP available — not enough for this item ({cost:0.##} DKP).";
        }

        return null;
    }
}
