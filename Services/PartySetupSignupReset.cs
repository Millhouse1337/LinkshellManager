using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Moved out of the old website's PartySetupController so it outlives
// it: TodRecorder calls it for every new ToD (the app, the addon, approval and End Camp). Self-committing.
public static class PartySetupSignupReset
{
    // A fresh ToD for a monster means the previous pop's roster is stale, so
    // every member sign-up on any party setup currently assigned to that
    // monster is released and the ToD Tracker's inline panel reopens empty for
    // the next window. Matches AssignedMonsterName case-insensitively (same
    // canonical name as Tod.MonsterName). No-op when nothing is assigned or
    // nobody is signed up; returns the number of slots cleared.
    // Template sign-ups can no longer be made (Phase 5), so this only clears
    // leftovers from before; it goes away with the DROP-LATER columns on
    // PartySetupSlot (see docs/phase-4b-drop-manifest.md, "Later drops").
    public static async Task<int> ClearSignupsForMonsterAsync(
        ApplicationDbContext context,
        int linkshellId,
        string? monsterName,
        CancellationToken cancellationToken = default)
    {
        var trimmed = monsterName?.Trim();
        if (linkshellId <= 0 || string.IsNullOrEmpty(trimmed))
        {
            return 0;
        }

        // Resolve the assigned setups first so the name match honors the
        // OrdinalIgnoreCase convention used everywhere else for monster names
        // (rather than relying on the provider's default string collation).
        var assigned = await context.PartySetups
            .Where(ps => ps.LinkshellId == linkshellId && ps.AssignedMonsterName != null && ps.OwnerEventId == null)
            .Select(ps => new { ps.Id, ps.AssignedMonsterName })
            .ToListAsync(cancellationToken);

        var setupIds = assigned
            .Where(ps => string.Equals(ps.AssignedMonsterName!.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
            .Select(ps => ps.Id)
            .ToList();
        if (setupIds.Count == 0)
        {
            return 0;
        }

        var heldSlots = await context.PartySetupSlots
            .Where(s => setupIds.Contains(s.Party!.Alliance!.PartySetupId)
                        && s.SignedUpAppUserId != null)
            .ToListAsync(cancellationToken);
        if (heldSlots.Count == 0)
        {
            return 0;
        }

        foreach (var slot in heldSlots)
        {
            slot.SignedUpAppUserId = null;
            slot.SignedUpCharacterName = null;
            slot.SignedUpAtUtc = null;
            slot.SignedUpRole = null;
            slot.SignedUpMainJob = null;
            slot.SignedUpSubJob = null;
        }

        await context.SaveChangesAsync(cancellationToken);
        return heldSlots.Count;
    }
}
