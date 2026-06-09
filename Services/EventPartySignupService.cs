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

    // Turns party-slot signups into live-event participations when the event
    // starts. Slot signups (from the Discord post or the Activity) live in
    // EventPartySlotSignups, but the live event rooms / timers / DKP / close all
    // run off AppUserEvents — so without this a signed-up member never appears in
    // the started event. Each signup that doesn't already have a participation for
    // this event becomes a pending (unverified) attendee carrying their slot's
    // role + job, with StartTime = the event's commencement, so they land in the
    // Attendance room and accrue from the start once a leader verifies them.
    // Requires eventEntity.AppUserEvents to be loaded and CommencementStartTime
    // set. Does NOT commit — the caller (the start action) owns SaveChanges.
    // Returns the number of participations created.
    public static async Task<int> MaterializeSignupsAsParticipantsAsync(
        ApplicationDbContext db, Event eventEntity, CancellationToken cancellationToken)
    {
        var signups = await db.EventPartySlotSignups
            .Where(s => s.EventId == eventEntity.Id && s.AppUserId != null)
            .ToListAsync(cancellationToken);
        if (signups.Count == 0)
        {
            return 0;
        }

        // Don't double-add anyone who already joined (e.g. "Join (no slot)").
        var alreadyParticipating = new HashSet<string>(
            eventEntity.AppUserEvents
                .Where(p => !string.IsNullOrEmpty(p.AppUserId))
                .Select(p => p.AppUserId!),
            StringComparer.Ordinal);

        var created = 0;
        foreach (var signup in signups)
        {
            if (signup.AppUserId is null || !alreadyParticipating.Add(signup.AppUserId))
            {
                continue;
            }

            eventEntity.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = signup.AppUserId,
                EventId = eventEntity.Id,
                CharacterName = signup.CharacterName,
                JobName = signup.MainJob,
                SubJobName = signup.SubJob,
                JobType = signup.Role,
                StartTime = eventEntity.CommencementStartTime,
                EventDkp = 0,
                // Pending → shows in the Attendance room until a leader verifies,
                // mirroring the Activity "Join" flow (IsVerified left null).
                IsVerified = null,
            });
            created++;
        }

        return created;
    }

    // Members may drop their own slot only BEFORE the event starts. Once live, a
    // slot can only be cleared by an officer (which also removes the participation),
    // so the running roster isn't disrupted by self-withdrawals mid-run.
    public static bool MemberCanWithdraw(Event eventEntity) => eventEntity.CommencementStartTime is null;

    // Called right after ClaimSlotAsync is COMMITTED (the slot signup must already
    // be persisted). Before the event starts, claiming a slot drops the member's
    // "no slot" attendance (one identity per event — they'll be materialized in
    // bulk at start). Once live, instead of dropping it we materialize/convert the
    // claim into a live participation immediately so the late joiner appears in the
    // running event. Does NOT commit — the caller owns SaveChanges.
    public static async Task SyncParticipationAfterClaimAsync(
        ApplicationDbContext db, Event eventEntity, string appUserId, CancellationToken cancellationToken)
    {
        if (eventEntity.CommencementStartTime is not null)
        {
            await MaterializeOneAsParticipantAsync(db, eventEntity, appUserId, cancellationToken);
            return;
        }

        // Pre-start: drop any attendance the member holds (replaced by the slot).
        var rows = await db.AppUserEvents
            .Where(p => p.EventId == eventEntity.Id && p.AppUserId == appUserId)
            .ToListAsync(cancellationToken);
        if (rows.Count > 0)
        {
            db.AppUserEvents.RemoveRange(rows);
        }
    }

    // Mid-event: turn ONE member's (already-persisted) slot signup into — or update
    // — their live participation, so a late slot claim shows up in the Attendance
    // room / DKP roster right away. A fresh join starts NOW (partial DKP, like a
    // quick join); if they already participate (e.g. a no-slot quick join) we adopt
    // the slot's job in place and keep their StartTime. Does NOT commit.
    public static async Task MaterializeOneAsParticipantAsync(
        ApplicationDbContext db, Event eventEntity, string appUserId, CancellationToken cancellationToken)
    {
        var signup = await db.EventPartySlotSignups
            .FirstOrDefaultAsync(s => s.EventId == eventEntity.Id && s.AppUserId == appUserId, cancellationToken);
        if (signup is null)
        {
            return;
        }

        var participation = await db.AppUserEvents
            .FirstOrDefaultAsync(p => p.EventId == eventEntity.Id && p.AppUserId == appUserId, cancellationToken);
        if (participation is null)
        {
            db.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = appUserId,
                EventId = eventEntity.Id,
                CharacterName = signup.CharacterName,
                JobName = signup.MainJob,
                SubJobName = signup.SubJob,
                JobType = signup.Role,
                StartTime = DateTime.UtcNow,
                EventDkp = 0,
                IsVerified = null,
                IsQuickJoin = true,
            });
        }
        else
        {
            // Already attending (no-slot quick join etc.) → adopt the slot's job.
            participation.CharacterName = signup.CharacterName ?? participation.CharacterName;
            participation.JobName = signup.MainJob;
            participation.SubJobName = signup.SubJob;
            participation.JobType = signup.Role;
        }
    }

    // Removes a member's live participation for an event (used when an officer
    // clears a slot mid-run so the board and the DKP roster stay consistent).
    // Does NOT commit — the caller owns SaveChanges.
    public static async Task RemoveParticipationAsync(
        ApplicationDbContext db, int eventId, string appUserId, CancellationToken cancellationToken)
    {
        var rows = await db.AppUserEvents
            .Where(p => p.EventId == eventId && p.AppUserId == appUserId)
            .ToListAsync(cancellationToken);
        if (rows.Count > 0)
        {
            db.AppUserEvents.RemoveRange(rows);
        }
    }

    // When an event's party setup changes, its slot signups are keyed to the OLD
    // setup's slots and would be orphaned. Rather than drop the members, move each
    // to the event's general "no slot" attendance (AppUserEvents) so they still
    // appear on the new board's "Also attending — no slot" line, carrying their
    // job. Members already on the no-slot roster aren't duplicated (matched by
    // AppUserId, or by character name for manual signups with no linked user).
    // Removes the slot signups. Does NOT commit — the caller owns SaveChanges.
    // Returns the number of members moved.
    public static async Task<int> MoveSlotSignupsToNoSlotAsync(
        ApplicationDbContext db, int eventId, DateTime? startTime, CancellationToken cancellationToken)
    {
        var signups = await db.EventPartySlotSignups
            .Where(s => s.EventId == eventId)
            .ToListAsync(cancellationToken);
        if (signups.Count == 0)
        {
            return 0;
        }

        var existing = await db.AppUserEvents
            .Where(p => p.EventId == eventId)
            .ToListAsync(cancellationToken);
        var attendingUserIds = new HashSet<string>(
            existing.Where(p => !string.IsNullOrEmpty(p.AppUserId)).Select(p => p.AppUserId!),
            StringComparer.Ordinal);
        var attendingNames = new HashSet<string>(
            existing.Where(p => !string.IsNullOrWhiteSpace(p.CharacterName)).Select(p => p.CharacterName!.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var moved = 0;
        foreach (var signup in signups)
        {
            var hasUser = !string.IsNullOrEmpty(signup.AppUserId);
            var hasName = !string.IsNullOrWhiteSpace(signup.CharacterName);
            // Skip anyone already attending "no slot" (don't double-add).
            if (hasUser && attendingUserIds.Contains(signup.AppUserId!))
            {
                continue;
            }
            if (!hasUser && hasName && attendingNames.Contains(signup.CharacterName!.Trim()))
            {
                continue;
            }

            db.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = signup.AppUserId,
                EventId = eventId,
                CharacterName = signup.CharacterName,
                JobName = signup.MainJob,
                SubJobName = signup.SubJob,
                JobType = signup.Role,
                StartTime = startTime,
                EventDkp = 0,
            });
            if (hasUser)
            {
                attendingUserIds.Add(signup.AppUserId!);
            }
            if (hasName)
            {
                attendingNames.Add(signup.CharacterName!.Trim());
            }
            moved++;
        }

        db.EventPartySlotSignups.RemoveRange(signups);
        return moved;
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
