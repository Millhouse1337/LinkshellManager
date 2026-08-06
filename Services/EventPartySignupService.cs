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
        string? appUserId,
        string characterName,
        string? requestedRole,
        string? requestedMainJob,
        string? requestedSubJob,
        CancellationToken cancellationToken,
        bool claimAsLeader = false,
        string? discordUserId = null)
    {
        // "Outside Party Signup" (no linked account) keys identity by Discord user id;
        // a normal signup keys by AppUserId. Exactly one of the two is set.
        var isOutside = string.IsNullOrEmpty(appUserId);

        var existing = await db.EventPartySlotSignups
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.PartySetupSlotId == slot.Id, cancellationToken);
        // A placeholder-matched claim carries BOTH ids; also treat the slot as "mine"
        // if it's a row this Discord user already holds (e.g. one they claimed as a
        // true-outside signup before the leader created their linkshell-only member),
        // so re-signing up adopts it instead of being blocked as "taken".
        var isMine = existing is not null && (isOutside
            ? existing.AppUserId == null && existing.DiscordUserId == discordUserId
            : existing.AppUserId == appUserId
              || (!string.IsNullOrEmpty(discordUserId) && existing.DiscordUserId == discordUserId));
        if (existing is not null && !isMine)
        {
            return new ClaimResult(false, $"That slot was just taken by {existing.CharacterName ?? "another member"}.");
        }

        var jobs = PartySetupSignupService.ResolveSignupJobs(slot, requestedRole, requestedMainJob, requestedSubJob);
        if (!jobs.Success)
        {
            return new ClaimResult(false, jobs.Error);
        }

        // One slot per event: release any OTHER slot the member holds in this event,
        // matched by whichever identity is in play — for a placeholder match that's the
        // placeholder's AppUserId OR the clicker's Discord id (catches a prior
        // true-outside slot the same person holds, so they're never double-slotted).
        var others = isOutside
            ? await db.EventPartySlotSignups
                .Where(s => s.EventId == eventId && s.PartySetupSlotId != slot.Id
                    && s.AppUserId == null && s.DiscordUserId == discordUserId)
                .ToListAsync(cancellationToken)
            : await db.EventPartySlotSignups
                .Where(s => s.EventId == eventId && s.PartySetupSlotId != slot.Id
                    && (s.AppUserId == appUserId
                        || (discordUserId != null && s.DiscordUserId == discordUserId)))
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
        // Stamp both ids (one is null) so a reused row never carries a stale identity.
        existing.AppUserId = appUserId;
        existing.DiscordUserId = discordUserId;
        existing.CharacterName = characterName;
        existing.Role = jobs.Role;
        existing.MainJob = jobs.MainJob;
        existing.SubJob = jobs.SubJob;
        existing.SignedUpAtUtc = DateTime.UtcNow;

        // Claiming the party's DESIGNATED leader slot opts you into leadership by default
        // (the same as the "Sign Up as Party Leader" button) — but leadership is no longer
        // LOCKED to that slot: anyone can take it later via "Make Me Party Lead". Either
        // path is first-claim-wins, so the crown only lands when the party has no leader
        // yet (the member's own slot is excluded, so re-signing keeps an existing crown).
        if (slot.IsPartyLeader || claimAsLeader)
        {
            var partyHasOtherLeader = await db.EventPartySlotSignups.AnyAsync(
                s => s.EventId == eventId
                     && s.PartySetupSlotId != slot.Id
                     && s.IsPartyLeader
                     && s.PartySetupSlot!.PartySetupPartyId == slot.PartySetupPartyId,
                cancellationToken);
            if (partyHasOtherLeader) { ClearLeadership(existing); }
            else { existing.IsPartyLeader = true; }
        }

        return new ClaimResult(true, null);
    }

    // Takes BOTH crowns off a signup.
    //
    // The alliance crown rides on the party crown: an alliance lead is always the
    // leader of their own party (that's what MakeAllianceLeaderAsync requires). So
    // every path that removes the party crown has to remove the alliance crown with
    // it — otherwise the board keeps naming an alliance lead who no longer leads a
    // party, and the ex-leader silently keeps a designation nobody can see they hold.
    //
    // The alliance is deliberately left WITHOUT a lead rather than handing it to the
    // incoming party leader: alliance lead is never auto-assigned, it's always an
    // explicit claim, and quietly promoting someone to lead all 18 slots is a much
    // bigger move than the party-lead change that triggered it.
    public static void ClearLeadership(EventPartySlotSignup signup)
    {
        signup.IsPartyLeader = false;
        signup.IsAllianceLeader = false;
    }

    // "Make Me Party Lead": the member — who must already hold a slot in this event
    // — takes their party's leadership, moving the 👑 OFF whoever currently holds it.
    // Identity is keyed by AppUserId (account signup) or DiscordUserId (board-only
    // signup). Unlike the first-claim-wins sign-up path, this DELIBERATELY overrides
    // an existing leader. Any member in the party may take it — there's no longer a
    // designated-leader-slot lock. Rejected only when the member holds no slot or
    // already leads. Does NOT commit — the caller owns SaveChanges. There's no
    // party-leadership re-resolution to run afterwards: the crown is set explicitly
    // here, so the party is never left leaderless.
    public static async Task<ClaimResult> MakePartyLeaderAsync(
        ApplicationDbContext db, int eventId, string? appUserId, string? discordUserId,
        CancellationToken cancellationToken)
    {
        var isOutside = string.IsNullOrEmpty(appUserId);
        var mine = isOutside
            // Board-only clicker: match by Discord id ALONE (mirrors LeaveAsync) so it
            // also finds a placeholder-matched slot (which carries a non-null AppUserId).
            ? await db.EventPartySlotSignups
                .Include(s => s.PartySetupSlot)
                .FirstOrDefaultAsync(s => s.EventId == eventId && s.DiscordUserId == discordUserId, cancellationToken)
            : await db.EventPartySlotSignups
                .Include(s => s.PartySetupSlot)
                .FirstOrDefaultAsync(s => s.EventId == eventId
                    && (s.AppUserId == appUserId
                        || (discordUserId != null && s.DiscordUserId == discordUserId)), cancellationToken);

        if (mine is null)
        {
            return new ClaimResult(false, "You need a party slot first — sign up, then make yourself the leader.");
        }
        if (mine.PartySetupSlot?.PartySetupPartyId is not { } partyId)
        {
            return new ClaimResult(false, "Couldn't find your party — try signing up again.");
        }
        if (mine.IsPartyLeader)
        {
            return new ClaimResult(false, "You're already this party's leader 👑.");
        }

        // Move the crown: clear it from any current holder(s) in this party, set it on
        // me. `mine` isn't a leader (checked above), so it's never in this set. The
        // outgoing leader also drops the alliance crown if they held it — see
        // ClearLeadership.
        var currentLeaders = await db.EventPartySlotSignups
            .Where(s => s.EventId == eventId
                        && s.PartySetupSlot!.PartySetupPartyId == partyId
                        && s.IsPartyLeader)
            .ToListAsync(cancellationToken);
        foreach (var leader in currentLeaders)
        {
            ClearLeadership(leader);
        }
        mine.IsPartyLeader = true;

        return new ClaimResult(true, null);
    }

    // "Make Me Alliance Lead": the member — who must already hold a slot in this event
    // — takes their ALLIANCE's lead (👑 by the alliance name), moving it OFF whoever
    // currently holds it in that alliance. One rung above MakePartyLeaderAsync (the
    // whole 18-slot group rather than a single party); otherwise identical: identity
    // keyed by AppUserId (account) or DiscordUserId (board-only), deliberately overrides
    // the current holder, purely a designation (no perms), never auto-assigned. Does NOT
    // commit — the caller owns SaveChanges.
    public static async Task<ClaimResult> MakeAllianceLeaderAsync(
        ApplicationDbContext db, int eventId, string? appUserId, string? discordUserId,
        CancellationToken cancellationToken)
    {
        var isOutside = string.IsNullOrEmpty(appUserId);
        var mine = isOutside
            ? await db.EventPartySlotSignups
                .Include(s => s.PartySetupSlot!).ThenInclude(slot => slot.Party!)
                .FirstOrDefaultAsync(s => s.EventId == eventId && s.DiscordUserId == discordUserId, cancellationToken)
            : await db.EventPartySlotSignups
                .Include(s => s.PartySetupSlot!).ThenInclude(slot => slot.Party!)
                .FirstOrDefaultAsync(s => s.EventId == eventId
                    && (s.AppUserId == appUserId
                        || (discordUserId != null && s.DiscordUserId == discordUserId)), cancellationToken);

        if (mine is null)
        {
            return new ClaimResult(false, "You need a party slot first — sign up, then make yourself the alliance lead.");
        }
        if (mine.PartySetupSlot?.Party?.PartySetupAllianceId is not { } allianceId)
        {
            return new ClaimResult(false, "Couldn't find your alliance — try signing up again.");
        }
        if (mine.IsAllianceLeader)
        {
            return new ClaimResult(false, "You're already this alliance's lead 👑.");
        }
        // Alliance lead is the party leaders' rung: you lead your party, and one of
        // the party leaders leads the alliance. Without this a plain member could
        // crown themselves over three party leaders, and the crown would then have
        // to survive on someone the party-lead paths have no reason to touch.
        if (!mine.IsPartyLeader)
        {
            return new ClaimResult(false, "Only a party leader can take the alliance lead — make yourself party lead first 👑.");
        }

        // Move the crown: clear it from any current holder(s) in this alliance, set it on
        // me. `mine` isn't a lead (checked above), so it's never in this set.
        var currentLeads = await db.EventPartySlotSignups
            .Where(s => s.EventId == eventId
                        && s.PartySetupSlot!.Party!.PartySetupAllianceId == allianceId
                        && s.IsAllianceLeader)
            .ToListAsync(cancellationToken);
        foreach (var lead in currentLeads)
        {
            lead.IsAllianceLeader = false;
        }
        mine.IsAllianceLeader = true;

        return new ClaimResult(true, null);
    }

    // Officer action: set the party-leader crown (👑) on the member occupying a
    // SPECIFIC slot (chosen by an officer), rather than the caller's own slot.
    // Mirrors MakePartyLeaderAsync but keys off slotId: clears the crown from any
    // current holder in that party and sets it on the chosen slot's signup. Does
    // NOT commit — the caller owns SaveChanges.
    public static async Task<ClaimResult> SetPartyLeaderBySlotAsync(
        ApplicationDbContext db, int eventId, int slotId, CancellationToken cancellationToken)
    {
        var target = await db.EventPartySlotSignups
            .Include(s => s.PartySetupSlot)
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.PartySetupSlotId == slotId, cancellationToken);
        if (target is null)
        {
            return new ClaimResult(false, "That member is no longer in that slot.");
        }
        if (target.PartySetupSlot?.PartySetupPartyId is not { } partyId)
        {
            return new ClaimResult(false, "Couldn't find that member's party.");
        }
        if (target.IsPartyLeader)
        {
            return new ClaimResult(false, "They're already this party's leader 👑.");
        }

        // As in MakePartyLeaderAsync, the outgoing leader drops the alliance crown
        // along with the party one — an officer reassigning party lead is exactly
        // the case where the old lead would otherwise keep a stale alliance crown.
        var currentLeaders = await db.EventPartySlotSignups
            .Where(s => s.EventId == eventId
                        && s.PartySetupSlot!.PartySetupPartyId == partyId
                        && s.IsPartyLeader)
            .ToListAsync(cancellationToken);
        foreach (var leader in currentLeaders)
        {
            ClearLeadership(leader);
        }
        target.IsPartyLeader = true;

        return new ClaimResult(true, null);
    }

    // "🔒 Stay Next Window" (member): toggles the "survives the Next Window wipe" lock on
    // the CLICKER's OWN slot in this event. Identity is keyed by AppUserId (account) or
    // DiscordUserId (board-only), mirroring MakePartyLeaderAsync. Returns the NEW locked
    // state, or null when the member holds no slot (nothing to lock). Does NOT commit —
    // the caller owns SaveChanges.
    public static async Task<bool?> ToggleStayNextWindowAsync(
        ApplicationDbContext db, int eventId, string? appUserId, string? discordUserId,
        CancellationToken cancellationToken)
    {
        var isOutside = string.IsNullOrEmpty(appUserId);
        var mine = isOutside
            ? await db.EventPartySlotSignups
                .FirstOrDefaultAsync(s => s.EventId == eventId && s.DiscordUserId == discordUserId, cancellationToken)
            : await db.EventPartySlotSignups
                .FirstOrDefaultAsync(s => s.EventId == eventId
                    && (s.AppUserId == appUserId
                        || (discordUserId != null && s.DiscordUserId == discordUserId)), cancellationToken);
        if (mine is null)
        {
            return null;
        }
        mine.StayNextWindow = !mine.StayNextWindow;
        return mine.StayNextWindow;
    }

    // "🔒 Lock Member" (officer): toggles the stay-next-window lock on the member occupying
    // a SPECIFIC slot (chosen by an officer), mirroring SetPartyLeaderBySlotAsync. Returns
    // success, the NEW locked state, and the member's name (for the confirmation line).
    // Does NOT commit — the caller owns SaveChanges.
    public static async Task<(bool Success, string? Error, bool Locked, string? Name)> SetStayNextWindowBySlotAsync(
        ApplicationDbContext db, int eventId, int slotId, CancellationToken cancellationToken)
    {
        var target = await db.EventPartySlotSignups
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.PartySetupSlotId == slotId, cancellationToken);
        if (target is null)
        {
            return (false, "That member is no longer in that slot.", false, null);
        }
        target.StayNextWindow = !target.StayNextWindow;
        return (true, null, target.StayNextWindow, target.CharacterName);
    }

    // Clears a windowed camp's roster for a window turnover: every slot signup and every no-slot
    // attendee goes, EXCEPT rows a member (or officer) pinned with "🔒 Stay Next Window", whose
    // AppUserEvent participation is spared too. StayNextWindow is deliberately NOT reset — the
    // lock persists into the next window until the member unlocks, withdraws, or is removed.
    //
    // `closingWindow` is the window the roster being wiped BELONGED to (the one that just ended,
    // not the one now open). Everything seated is copied into EventWindowRosterSnapshot under that
    // number first, which is what "View Previous Window" reads back — so the capture can never
    // drift from the wipe, because it happens here or not at all. Pass a number below 1 to skip the
    // snapshot (a caller with no meaningful window to attribute it to).
    //
    // Called by the automatic window advance; the caller decides WHETHER a clear applies (wyrms
    // only, Standard mode) and owns SaveChanges.
    public static async Task ClearWindowRosterAsync(
        ApplicationDbContext db, int eventId, int closingWindow, CancellationToken cancellationToken)
    {
        // Walk to the alliance so the snapshot can copy the grouping labels: the party setup is a
        // reusable template whose slots are REBUILT on edit, so ids captured here would rot.
        var slotSignups = await db.EventPartySlotSignups
            .Include(s => s.PartySetupSlot!).ThenInclude(slot => slot.Party!).ThenInclude(p => p.Alliance)
            .Where(s => s.EventId == eventId)
            .ToListAsync(cancellationToken);

        if (closingWindow >= 1 && slotSignups.Count > 0)
        {
            var capturedAt = DateTime.UtcNow;
            db.EventWindowRosterSnapshots.AddRange(slotSignups.Select(s => new EventWindowRosterSnapshot
            {
                EventId = eventId,
                WindowNumber = closingWindow,
                CapturedAtUtc = capturedAt,
                AllianceName = s.PartySetupSlot?.Party?.Alliance?.Name,
                AllianceSortOrder = s.PartySetupSlot?.Party?.Alliance?.SortOrder ?? 0,
                PartyName = s.PartySetupSlot?.Party?.Name,
                PartySortOrder = s.PartySetupSlot?.Party?.SortOrder ?? 0,
                SlotSortOrder = s.PartySetupSlot?.SortOrder ?? 0,
                SlotLabel = s.PartySetupSlot?.Label,
                AppUserId = s.AppUserId,
                CharacterName = s.CharacterName,
                Role = s.Role,
                MainJob = s.MainJob,
                SubJob = s.SubJob,
                IsPartyLeader = s.IsPartyLeader,
                IsAllianceLeader = s.IsAllianceLeader,
                WasLocked = s.StayNextWindow,
            }));
        }

        var keptSignups = slotSignups.Where(s => s.StayNextWindow).ToList();
        db.EventPartySlotSignups.RemoveRange(slotSignups.Where(s => !s.StayNextWindow));

        // Spare the participation of anyone whose slot is staying (locked outside/Discord-only
        // signups have no AppUserEvent, so in practice this only ever keeps account rows).
        var keptAppUserIds = keptSignups.Where(s => s.AppUserId != null).Select(s => s.AppUserId!).ToHashSet();
        var keptDiscordIds = keptSignups.Where(s => s.DiscordUserId != null).Select(s => s.DiscordUserId!).ToHashSet();
        var attendees = await db.AppUserEvents
            .Where(p => p.EventId == eventId)
            .ToListAsync(cancellationToken);
        db.AppUserEvents.RemoveRange(attendees.Where(p =>
            !(p.AppUserId != null && keptAppUserIds.Contains(p.AppUserId))
            && !(p.DiscordUserId != null && keptDiscordIds.Contains(p.DiscordUserId))));
    }

    // Releases whatever slot the member holds in this event. Returns the party id
    // the member left (so the caller can re-resolve that party's leadership), or
    // null if they held no slot.
    public static async Task<int?> LeaveAsync(
        ApplicationDbContext db, int eventId, string? appUserId, CancellationToken cancellationToken,
        string? discordUserId = null)
    {
        var isOutside = string.IsNullOrEmpty(appUserId);
        var held = isOutside
            // Outside clicker: match by Discord id ALONE (not also AppUserId == null) so
            // it also finds a placeholder-matched slot, which carries a non-null AppUserId.
            ? await db.EventPartySlotSignups
                .Include(s => s.PartySetupSlot)
                .Where(s => s.EventId == eventId && s.DiscordUserId == discordUserId)
                .ToListAsync(cancellationToken)
            : await db.EventPartySlotSignups
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

            var participationStartTime = eventEntity.CommencementStartTime;
            if (participationStartTime.HasValue && signup.SignedUpAtUtc > participationStartTime.Value)
            {
                participationStartTime = signup.SignedUpAtUtc;
            }

            eventEntity.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = signup.AppUserId,
                // Carry the slot's Discord id too (dual-stamp), so a Discord-board
                // withdrawal — which matches an unsynced/placeholder member by Discord
                // id — can find and drop this materialized participation.
                DiscordUserId = signup.DiscordUserId,
                EventId = eventEntity.Id,
                CharacterName = signup.CharacterName,
                JobName = signup.MainJob,
                SubJobName = signup.SubJob,
                JobType = signup.Role,
                StartTime = participationStartTime,
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
        ApplicationDbContext db, Event eventEntity, string? appUserId, CancellationToken cancellationToken,
        string? discordUserId = null)
    {
        var isOutside = string.IsNullOrEmpty(appUserId);

        // Outside (no-account) signups are NEVER materialized into live participation —
        // they have no account to accrue attendance/DKP, and stay board-only. Pre-start
        // we still drop any stray no-slot attendance they hold (one identity per event).
        if (isOutside)
        {
            if (eventEntity.CommencementStartTime is not null)
            {
                return;
            }
            var outsideRows = await db.AppUserEvents
                .Where(p => p.EventId == eventEntity.Id && p.AppUserId == null && p.DiscordUserId == discordUserId)
                .ToListAsync(cancellationToken);
            if (outsideRows.Count > 0)
            {
                db.AppUserEvents.RemoveRange(outsideRows);
            }
            return;
        }

        if (eventEntity.CommencementStartTime is not null)
        {
            await MaterializeOneAsParticipantAsync(db, eventEntity, appUserId!, cancellationToken);
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
                // Dual-stamp the Discord id (see MaterializeSignupsAsParticipantsAsync)
                // so a placeholder member's Discord-board withdrawal can drop this row.
                DiscordUserId = signup.DiscordUserId,
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

    // Officer action: remove a member from an event ENTIRELY — drop their party
    // slot (if any) AND their participation/attendance (the DKP roster row). Works
    // for account members (appUserId) and board-only members (discordUserId).
    // Generalizes RemoveParticipationAsync to either identity. Self-committing, and
    // re-resolves leadership for the freed party afterwards.
    public static async Task RemoveMemberCompletelyAsync(
        ApplicationDbContext db, int eventId, string? appUserId, string? discordUserId,
        CancellationToken cancellationToken)
    {
        // Slot first (LeaveAsync matches by AppUserId, or by Discord id for a
        // board-only member), then their attendance row.
        var affectedPartyId = await LeaveAsync(db, eventId, appUserId, cancellationToken, discordUserId);

        var participation = string.IsNullOrEmpty(appUserId)
            ? await db.AppUserEvents
                .Where(p => p.EventId == eventId && p.DiscordUserId == discordUserId)
                .ToListAsync(cancellationToken)
            : await db.AppUserEvents
                .Where(p => p.EventId == eventId && p.AppUserId == appUserId)
                .ToListAsync(cancellationToken);
        if (participation.Count > 0)
        {
            db.AppUserEvents.RemoveRange(participation);
        }

        await db.SaveChangesAsync(cancellationToken);
        await ResolvePartyLeadershipAsync(db, eventId, affectedPartyId, cancellationToken);
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
            if (ConvertSignupToNoSlot(db, eventId, signup, startTime, attendingUserIds, attendingNames))
            {
                moved++;
            }
        }

        db.EventPartySlotSignups.RemoveRange(signups);
        return moved;
    }

    // Moves ONE slot signup to "no slot" (the board's "Also Attending"): the member
    // stays in the event with their job, but the party slot is freed. Pre-start there's
    // no participation yet, so a no-slot AppUserEvent is created; once the event is LIVE
    // the member already has a materialized AppUserEvent (left untouched ⇒ no StartTime
    // / DKP change), so only the slot signup is dropped. Removes the slot signup. Returns
    // the party id the slot was in (for leadership re-resolution). Does NOT commit.
    public static async Task<int?> MoveSlotSignupToNoSlotAsync(
        ApplicationDbContext db, int eventId, EventPartySlotSignup signup, DateTime? startTime, CancellationToken cancellationToken)
    {
        var partyId = await db.PartySetupSlots
            .Where(s => s.Id == signup.PartySetupSlotId)
            .Select(s => (int?)s.PartySetupPartyId)
            .FirstOrDefaultAsync(cancellationToken);

        var existing = await db.AppUserEvents
            .Where(p => p.EventId == eventId)
            .ToListAsync(cancellationToken);
        var attendingUserIds = new HashSet<string>(
            existing.Where(p => !string.IsNullOrEmpty(p.AppUserId)).Select(p => p.AppUserId!),
            StringComparer.Ordinal);
        var attendingNames = new HashSet<string>(
            existing.Where(p => !string.IsNullOrWhiteSpace(p.CharacterName)).Select(p => p.CharacterName!.Trim()),
            StringComparer.OrdinalIgnoreCase);

        ConvertSignupToNoSlot(db, eventId, signup, startTime, attendingUserIds, attendingNames);
        db.EventPartySlotSignups.Remove(signup);
        return partyId;
    }

    // Adds a no-slot AppUserEvent mirroring `signup` UNLESS that member is already
    // attending no-slot (deduped via the two sets, which it updates). Does not remove
    // the signup or commit. Returns true if a row was added.
    private static bool ConvertSignupToNoSlot(
        ApplicationDbContext db, int eventId, EventPartySlotSignup signup, DateTime? startTime,
        HashSet<string> attendingUserIds, HashSet<string> attendingNames)
    {
        var hasUser = !string.IsNullOrEmpty(signup.AppUserId);
        var hasName = !string.IsNullOrWhiteSpace(signup.CharacterName);
        // Skip anyone already attending "no slot" (don't double-add).
        if (hasUser && attendingUserIds.Contains(signup.AppUserId!))
        {
            return false;
        }
        if (!hasUser && hasName && attendingNames.Contains(signup.CharacterName!.Trim()))
        {
            return false;
        }

        db.AppUserEvents.Add(new AppUserEvent
        {
            AppUserId = signup.AppUserId,
            // Carry the Discord identity so an outside signup can still withdraw
            // (and stays one-per-event) after being moved to no-slot.
            DiscordUserId = signup.DiscordUserId,
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
