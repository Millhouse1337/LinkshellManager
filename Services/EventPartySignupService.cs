using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Per-event party-setup slot signups. A party setup is a reusable template, so a
// signup belongs to a specific EVENT, not the shared template slot — this is what
// keeps one event's roster from bleeding onto every event that links the same
// setup. Stored in EventPartySlotSignups keyed by (EventId, PartySetupSlotId).
//
// Job-pick validation is shared with the template-slot path via
// PartySetupSignupService.ResolveSignupJobs. None of these commit — the caller
// owns SaveChanges (so a controller/interaction can batch with its other work).
public static class EventPartySignupService
{
    public sealed record ClaimResult(bool Success, string? Error);

    // Claims `slot` for the member in `eventId`. Caller has loaded `slot` (its
    // Role/MainJob/SubJob pins are read) and verified linkshell membership. When
    // `claimAsLeader` is true the member is also made the party's leader, unless
    // the party already has one (first-claim-wins — the slot claim still
    // succeeds, they just join as a regular member). Does NOT commit; the caller
    // owns SaveChanges and should then call ResolvePartyLeadershipAsync so a
    // now-full leaderless party auto-promotes its earliest signup.
    public static async Task<ClaimResult> ClaimSlotAsync(
        ApplicationDbContext db,
        int eventId,
        PartySetupSlot slot,
        string appUserId,
        string characterName,
        string? requestedRole,
        string? requestedMainJob,
        string? requestedSubJob,
        CancellationToken cancellationToken,
        bool claimAsLeader = false)
    {
        var existing = await db.EventPartySlotSignups
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.PartySetupSlotId == slot.Id, cancellationToken);
        if (existing is not null && existing.AppUserId != appUserId)
        {
            return new ClaimResult(false, $"That slot was just taken by {existing.CharacterName ?? "another member"}.");
        }

        var jobs = PartySetupSignupService.ResolveSignupJobs(slot, requestedRole, requestedMainJob, requestedSubJob);
        if (!jobs.Success)
        {
            return new ClaimResult(false, jobs.Error);
        }

        // One slot per event: release any other slot the member holds in this event.
        var others = await db.EventPartySlotSignups
            .Where(s => s.EventId == eventId && s.AppUserId == appUserId && s.PartySetupSlotId != slot.Id)
            .ToListAsync(cancellationToken);
        if (others.Count > 0)
        {
            db.EventPartySlotSignups.RemoveRange(others);
        }

        if (existing is null)
        {
            existing = new EventPartySlotSignup { EventId = eventId, PartySetupSlotId = slot.Id };
            db.EventPartySlotSignups.Add(existing);
        }
        existing.AppUserId = appUserId;
        existing.CharacterName = characterName;
        existing.Role = jobs.Role;
        existing.MainJob = jobs.MainJob;
        existing.SubJob = jobs.SubJob;
        existing.SignedUpAtUtc = DateTime.UtcNow;

        if (claimAsLeader)
        {
            // First-claim-wins: only grant leadership if the party has no other
            // leader yet (the member's own slot is excluded so re-signing keeps it).
            var partyHasOtherLeader = await db.EventPartySlotSignups.AnyAsync(
                s => s.EventId == eventId
                     && s.PartySetupSlotId != slot.Id
                     && s.IsPartyLeader
                     && s.PartySetupSlot!.PartySetupPartyId == slot.PartySetupPartyId,
                cancellationToken);
            existing.IsPartyLeader = !partyHasOtherLeader;
        }

        return new ClaimResult(true, null);
    }

    // Releases whatever slot the member holds in this event. Returns the party id
    // the member left (so the caller can re-resolve that party's leadership), or
    // null if they held no slot.
    public static async Task<int?> LeaveAsync(
        ApplicationDbContext db, int eventId, string appUserId, CancellationToken cancellationToken)
    {
        var held = await db.EventPartySlotSignups
            .Include(s => s.PartySetupSlot)
            .Where(s => s.EventId == eventId && s.AppUserId == appUserId)
            .ToListAsync(cancellationToken);
        if (held.Count == 0)
        {
            return null;
        }
        var affectedPartyId = held[0].PartySetupSlot?.PartySetupPartyId;
        db.EventPartySlotSignups.RemoveRange(held);
        return affectedPartyId;
    }

    // After a claim/leave is committed, ensures a party that is now FULL has a
    // leader: if none was claimed, the party's earliest signup is promoted. A
    // no-op when the party isn't full or already has a leader. Self-committing
    // (reads fresh, saves only when it changes something) so callers can fire it
    // right after their own SaveChanges. `partyId` may be null (no-op).
    public static async Task ResolvePartyLeadershipAsync(
        ApplicationDbContext db, int eventId, int? partyId, CancellationToken cancellationToken)
    {
        if (partyId is not { } pid)
        {
            return;
        }

        var slotCount = await db.PartySetupSlots.CountAsync(s => s.PartySetupPartyId == pid, cancellationToken);
        if (slotCount == 0)
        {
            return;
        }

        var partySignups = await db.EventPartySlotSignups
            .Where(s => s.EventId == eventId && s.PartySetupSlot!.PartySetupPartyId == pid)
            .ToListAsync(cancellationToken);

        // Already has a leader, or the party isn't full yet → nothing to do.
        if (partySignups.Any(s => s.IsPartyLeader) || partySignups.Count < slotCount)
        {
            return;
        }

        var earliest = partySignups
            .OrderBy(s => s.SignedUpAtUtc)
            .ThenBy(s => s.Id)
            .FirstOrDefault();
        if (earliest is not null)
        {
            earliest.IsPartyLeader = true;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // Map of PartySetupSlotId -> the event's signup for that slot, for rendering.
    public static async Task<Dictionary<int, EventPartySlotSignup>> GetSignupsForEventAsync(
        ApplicationDbContext db, int eventId, CancellationToken cancellationToken)
    {
        var rows = await db.EventPartySlotSignups
            .AsNoTracking()
            .Where(s => s.EventId == eventId)
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(s => s.PartySetupSlotId, s => s);
    }
}
