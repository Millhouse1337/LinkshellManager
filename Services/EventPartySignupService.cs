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
    // Role/MainJob/SubJob pins are read) and verified linkshell membership.
    public static async Task<ClaimResult> ClaimSlotAsync(
        ApplicationDbContext db,
        int eventId,
        PartySetupSlot slot,
        string appUserId,
        string characterName,
        string? requestedRole,
        string? requestedMainJob,
        string? requestedSubJob,
        CancellationToken cancellationToken)
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

        return new ClaimResult(true, null);
    }

    // Releases whatever slot the member holds in this event. Returns true if a
    // signup was removed.
    public static async Task<bool> LeaveAsync(
        ApplicationDbContext db, int eventId, string appUserId, CancellationToken cancellationToken)
    {
        var held = await db.EventPartySlotSignups
            .Where(s => s.EventId == eventId && s.AppUserId == appUserId)
            .ToListAsync(cancellationToken);
        if (held.Count == 0)
        {
            return false;
        }
        db.EventPartySlotSignups.RemoveRange(held);
        return true;
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
