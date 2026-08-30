using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Live, per-event party-board editing by officers. The board is rendered from a SHARED,
// reusable PartySetup template, but editing a live event's board must NOT mutate that
// template (or other events using it). So the first time an event's board is edited we
// CLONE its template into a private snapshot owned by the event (PartySetup.OwnerEventId),
// remap the event's existing slot signups onto the clone, and repoint Event.PartySetupId
// at it. Every later edit mutates the clone in place (slot Ids preserved => the roster
// survives). The snapshot is cascade-deleted when the event ends/cancels.
//
// These methods are SELF-COMMITTING transaction scripts (so both the web and Activity
// endpoints stay thin and identical): each clones-if-needed, mutates, saves, re-syncs
// participation / re-resolves party leadership as required. After any mutation the caller
// just enqueues the Discord board refresh and tells the client to reload the board.
public static class EventPartyBoardEditService
{
    public const int MaxSlotsPerParty = 6;
    public const int MaxPartiesPerAlliance = 3;

    // Canonical RequirementType values (mirror ViewModels.PartySetupSlotRequirementTypes,
    // not referenced here to keep this service free of a ViewModels dependency).
    private const string ReqAny = "Any";
    private const string ReqRole = "Role";
    private const string ReqJob = "Job";

    private static readonly HashSet<string> ValidRoles = new(EventJobCatalog.JobTypeOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidMainJobs = new(EventJobCatalog.MainJobOptions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ValidSubJobs = new(EventJobCatalog.SubJobOptions, StringComparer.OrdinalIgnoreCase);

    public sealed record EditResult(bool Success, string? Error);

    // Result of EnsureEventOwnedSetupAsync. On the FIRST edit it clones the template, so
    // every id changes — the maps translate the client's pre-clone ids (it rendered the
    // board from the shared template) to the clone's ids for THAT first request. On a
    // later edit (already owned) the maps are empty (ids unchanged). SetupId is null when
    // the event has no party setup at all.
    public sealed record EnsureResult(
        int? SetupId,
        IReadOnlyDictionary<int, int> SlotIdMap,
        IReadOnlyDictionary<int, int> PartyIdMap,
        IReadOnlyDictionary<int, int> AllianceIdMap);

    private static readonly IReadOnlyDictionary<int, int> EmptyMap = new Dictionary<int, int>();

    // Ensures the event owns a private copy of its party setup, cloning + remapping on
    // first call. Idempotent and self-committing.
    public static async Task<EnsureResult> EnsureEventOwnedSetupAsync(
        ApplicationDbContext db, int eventId, CancellationToken cancellationToken)
    {
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken)
            ?? throw new InvalidOperationException($"Event {eventId} not found.");
        if (ev.PartySetupId is not { } setupId)
        {
            return new EnsureResult(null, EmptyMap, EmptyMap, EmptyMap);
        }

        var setup = await db.PartySetups
            .Include(ps => ps.Alliances).ThenInclude(a => a.Parties).ThenInclude(p => p.Slots)
            .FirstOrDefaultAsync(ps => ps.Id == setupId, cancellationToken);
        if (setup is null)
        {
            return new EnsureResult(null, EmptyMap, EmptyMap, EmptyMap);
        }
        // Already this event's private copy → nothing to clone. (An event's PartySetupId
        // only ever points at a shared template or its OWN snapshot, so OwnerEventId !=
        // eventId here means `setup` is a shared template.)
        if (setup.OwnerEventId == eventId)
        {
            return new EnsureResult(setup.Id, EmptyMap, EmptyMap, EmptyMap);
        }

        var clone = new PartySetup
        {
            LinkshellId = setup.LinkshellId,
            Name = setup.Name,
            EventType = setup.EventType,
            AssignedMonsterName = null, // a private snapshot must never join a ToD row
            Notes = setup.Notes,
            CreatedByAppUserId = setup.CreatedByAppUserId,
            CreatedByCharacterName = setup.CreatedByCharacterName,
            CreatedAt = setup.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            OwnerEventId = eventId,
            // Remember where this came from. The snapshot dies with its event, so without this
            // the first drag-drop on a live board permanently severs the camp's link to the
            // shared template — and the next pop has nothing to inherit. A template cloning
            // from another template can't happen (only live boards clone), so this is only ever
            // set on snapshots. Chains are not followed: a snapshot's origin is a template by
            // construction, since setup here is what the event pointed at.
            ClonedFromPartySetupId = setup.OwnerEventId == null ? setup.Id : setup.ClonedFromPartySetupId,
        };

        // old template id -> new clone entity, to remap signups + translate the request.
        var allianceEntities = new Dictionary<int, PartySetupAlliance>();
        var partyEntities = new Dictionary<int, PartySetupParty>();
        var slotEntities = new Dictionary<int, PartySetupSlot>();
        foreach (var allianceT in setup.Alliances.OrderBy(a => a.SortOrder))
        {
            var alliance = new PartySetupAlliance { SortOrder = allianceT.SortOrder, Name = allianceT.Name };
            foreach (var partyT in allianceT.Parties.OrderBy(p => p.SortOrder))
            {
                var party = new PartySetupParty { SortOrder = partyT.SortOrder, Name = partyT.Name };
                foreach (var slotT in partyT.Slots.OrderBy(s => s.SortOrder))
                {
                    var slot = new PartySetupSlot
                    {
                        SortOrder = slotT.SortOrder,
                        RequirementType = slotT.RequirementType,
                        Role = slotT.Role,
                        MainJob = slotT.MainJob,
                        SubJob = slotT.SubJob,
                        Label = slotT.Label,
                        IsPartyLeader = slotT.IsPartyLeader,
                        // Template SignedUp* fields are intentionally NOT copied — the
                        // event roster lives in EventPartySlotSignup, not on the slot.
                    };
                    party.Slots.Add(slot);
                    slotEntities[slotT.Id] = slot;
                }
                alliance.Parties.Add(party);
                partyEntities[partyT.Id] = party;
            }
            clone.Alliances.Add(alliance);
            allianceEntities[allianceT.Id] = alliance;
        }

        db.PartySetups.Add(clone);
        try
        {
            // Persist so the clone's rows get Ids (needed to remap signups + build maps).
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent first-edit already created the snapshot (unique OwnerEventId
            // index). Detach our aborted clone subtree so it isn't re-inserted on the
            // next SaveChanges, reload the event, and use the existing copy. The other
            // request did the remap; the ids the client sent map to that copy, which we
            // can't translate here — the client reloads after the edit, so a rare lost
            // race just means this one edit no-ops against stale ids and the reload wins.
            DetachCloneSubtree(db, clone);
            await db.Entry(ev).ReloadAsync(cancellationToken);
            return new EnsureResult(ev.PartySetupId, EmptyMap, EmptyMap, EmptyMap);
        }

        // Remap this event's existing slot signups from old template slot ids to the
        // clone's new slot ids (preserves the whole roster across the repoint).
        var signups = await db.EventPartySlotSignups
            .Where(s => s.EventId == eventId)
            .ToListAsync(cancellationToken);
        foreach (var signup in signups)
        {
            if (slotEntities.TryGetValue(signup.PartySetupSlotId, out var newSlot))
            {
                signup.PartySetupSlotId = newSlot.Id;
            }
        }

        ev.PartySetupId = clone.Id;
        await db.SaveChangesAsync(cancellationToken);

        return new EnsureResult(
            clone.Id,
            slotEntities.ToDictionary(kv => kv.Key, kv => kv.Value.Id),
            partyEntities.ToDictionary(kv => kv.Key, kv => kv.Value.Id),
            allianceEntities.ToDictionary(kv => kv.Key, kv => kv.Value.Id));
    }

    // --- Edit operations (self-committing) --------------------------------------------

    // Change a slot's job/requirement in place (keeps the slot Id => signups survive). If
    // the slot is occupied AND the requirement actually changed, the occupant is moved to
    // "Also Attending" first (keeps their DKP when the event is live).
    public static async Task<EditResult> ChangeSlotRequirementAsync(
        ApplicationDbContext db, int eventId, int slotId,
        string? role, string? mainJob, string? subJob, CancellationToken cancellationToken)
    {
        var ensure = await EnsureEventOwnedSetupAsync(db, eventId, cancellationToken);
        if (ensure.SetupId is not { } setupId) { return NoSetup(); }
        slotId = Translate(ensure.SlotIdMap, slotId);

        var ev = await db.Events.FirstAsync(e => e.Id == eventId, cancellationToken);
        var slot = await LoadSlotAsync(db, setupId, slotId, cancellationToken);
        if (slot is null) { return new EditResult(false, "Slot not found."); }

        var (newType, newRole, newMain, newSub) = DeriveRequirement(role, mainJob, subJob);
        var changed = !Eq(slot.RequirementType, newType) || !Eq(slot.Role, newRole)
            || !Eq(slot.MainJob, newMain) || !Eq(slot.SubJob, newSub);
        if (!changed) { return new EditResult(true, null); }

        var occupant = await db.EventPartySlotSignups
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.PartySetupSlotId == slot.Id, cancellationToken);
        if (occupant is not null)
        {
            await EventPartySignupService.MoveSlotSignupToNoSlotAsync(
                db, eventId, occupant, ev.CommencementStartTime ?? ev.StartTime, cancellationToken);
        }
        slot.RequirementType = newType;
        slot.Role = newRole;
        slot.MainJob = newMain;
        slot.SubJob = newSub;
        await db.SaveChangesAsync(cancellationToken);
        return new EditResult(true, null);
    }

    // Reorder a slot within its party, or move it (carrying its occupant — no job change
    // ⇒ no displacement) to another party. SortOrder is recompacted in both parties.
    public static async Task<EditResult> MoveSlotAsync(
        ApplicationDbContext db, int eventId, int slotId, int targetPartyId, int targetIndex, CancellationToken cancellationToken)
    {
        var ensure = await EnsureEventOwnedSetupAsync(db, eventId, cancellationToken);
        if (ensure.SetupId is not { } setupId) { return NoSetup(); }
        slotId = Translate(ensure.SlotIdMap, slotId);
        targetPartyId = Translate(ensure.PartyIdMap, targetPartyId);

        var slot = await LoadSlotAsync(db, setupId, slotId, cancellationToken);
        if (slot is null) { return new EditResult(false, "Slot not found."); }
        var targetParty = await db.PartySetupParties
            .FirstOrDefaultAsync(p => p.Id == targetPartyId && p.Alliance!.PartySetupId == setupId, cancellationToken);
        if (targetParty is null) { return new EditResult(false, "Target party not found."); }

        var sourcePartyId = slot.PartySetupPartyId;
        if (sourcePartyId == targetPartyId)
        {
            var slots = await db.PartySetupSlots
                .Where(s => s.PartySetupPartyId == targetPartyId).OrderBy(s => s.SortOrder).ToListAsync(cancellationToken);
            slots.RemoveAll(s => s.Id == slot.Id);
            slots.Insert(Math.Clamp(targetIndex, 0, slots.Count), slot);
            for (var i = 0; i < slots.Count; i++) { slots[i].SortOrder = i; }
            await db.SaveChangesAsync(cancellationToken);
            return new EditResult(true, null);
        }

        var targetSlots = await db.PartySetupSlots
            .Where(s => s.PartySetupPartyId == targetPartyId).OrderBy(s => s.SortOrder).ToListAsync(cancellationToken);
        if (targetSlots.Count >= MaxSlotsPerParty)
        {
            return new EditResult(false, $"A party can hold at most {MaxSlotsPerParty} slots.");
        }

        // A slot dragged to a different party loses any party-leader crown (it would
        // otherwise carry leadership into a party that may already have one) and, with
        // it, any alliance crown — which must not ride along into another alliance
        // either. An officer can re-designate. Leadership for both parties is then
        // re-resolved.
        var movedSignup = await db.EventPartySlotSignups
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.PartySetupSlotId == slot.Id, cancellationToken);
        if (movedSignup is not null) { EventPartySignupService.ClearLeadership(movedSignup); }

        slot.PartySetupPartyId = targetPartyId;
        targetSlots.Insert(Math.Clamp(targetIndex, 0, targetSlots.Count), slot);
        for (var i = 0; i < targetSlots.Count; i++) { targetSlots[i].SortOrder = i; }
        var sourceSlots = await db.PartySetupSlots
            .Where(s => s.PartySetupPartyId == sourcePartyId && s.Id != slot.Id).OrderBy(s => s.SortOrder).ToListAsync(cancellationToken);
        for (var i = 0; i < sourceSlots.Count; i++) { sourceSlots[i].SortOrder = i; }

        await db.SaveChangesAsync(cancellationToken);
        return new EditResult(true, null);
    }

    // Move a MEMBER: from a slot to an empty slot, from a slot to "Also Attending", or
    // from "Also Attending" into an empty slot. Reuses ClaimSlotAsync (which releases the
    // member's old slot) and the no-slot conversion so DKP is preserved on a live event.
    public static async Task<EditResult> MoveMemberAsync(
        ApplicationDbContext db, int eventId, int? fromSlotId, int? toSlotId,
        string? appUserId, string? discordUserId, CancellationToken cancellationToken)
    {
        var ensure = await EnsureEventOwnedSetupAsync(db, eventId, cancellationToken);
        if (ensure.SetupId is not { } setupId) { return NoSetup(); }
        if (fromSlotId is { } f) { fromSlotId = Translate(ensure.SlotIdMap, f); }
        if (toSlotId is { } t) { toSlotId = Translate(ensure.SlotIdMap, t); }

        var ev = await db.Events.FirstAsync(e => e.Id == eventId, cancellationToken);
        var startTime = ev.CommencementStartTime ?? ev.StartTime;

        // Move to "Also Attending": just free the source slot (keep DKP when live).
        if (toSlotId is null)
        {
            if (fromSlotId is not { } fromId) { return new EditResult(false, "Nothing to move."); }
            var src = await db.EventPartySlotSignups
                .FirstOrDefaultAsync(s => s.EventId == eventId && s.PartySetupSlotId == fromId, cancellationToken);
            if (src is null) { return new EditResult(false, "That slot is no longer occupied."); }
            await EventPartySignupService.MoveSlotSignupToNoSlotAsync(db, eventId, src, startTime, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new EditResult(true, null);
        }

        var target = await LoadSlotAsync(db, setupId, toSlotId.Value, cancellationToken);
        if (target is null) { return new EditResult(false, "Target slot not found."); }
        var targetTaken = await db.EventPartySlotSignups
            .AnyAsync(s => s.EventId == eventId && s.PartySetupSlotId == target.Id, cancellationToken);
        if (targetTaken) { return new EditResult(false, "That slot is already taken."); }

        // Resolve the moving member's identity + job from their source slot signup, or
        // (if dragged from Also Attending) from their no-slot participation row.
        string? mAppUser = appUserId, mDiscord = discordUserId, mName = null, mRole = null, mMain = null, mSub = null;
        if (fromSlotId is { } fid)
        {
            var src = await db.EventPartySlotSignups
                .FirstOrDefaultAsync(s => s.EventId == eventId && s.PartySetupSlotId == fid, cancellationToken);
            if (src is null) { return new EditResult(false, "That slot is no longer occupied."); }
            (mAppUser, mDiscord, mName, mRole, mMain, mSub) = (src.AppUserId, src.DiscordUserId, src.CharacterName, src.Role, src.MainJob, src.SubJob);
        }
        else
        {
            var att = await db.AppUserEvents.FirstOrDefaultAsync(
                p => p.EventId == eventId && (mAppUser != null ? p.AppUserId == mAppUser : p.DiscordUserId == mDiscord),
                cancellationToken);
            if (att is null) { return new EditResult(false, "That attendee isn't in this event."); }
            (mAppUser, mDiscord, mName, mRole, mMain, mSub) = (att.AppUserId, att.DiscordUserId, att.CharacterName, att.JobType, att.JobName, att.SubJobName);
        }

        var claim = await EventPartySignupService.ClaimSlotAsync(
            db, eventId, target, mAppUser, mName ?? "Member", mRole, mMain, mSub,
            cancellationToken, claimAsLeader: false, discordUserId: mDiscord);
        if (!claim.Success) { return new EditResult(false, claim.Error); }
        await db.SaveChangesAsync(cancellationToken);

        // Pre-start: drop their no-slot attendance. Live: materialize/keep the claim as a
        // participation (adopts the existing AppUserEvent ⇒ no StartTime / DKP change).
        await EventPartySignupService.SyncParticipationAfterClaimAsync(db, ev, mAppUser, cancellationToken, mDiscord);
        await db.SaveChangesAsync(cancellationToken);

        return new EditResult(true, null);
    }

    // Add an empty slot to a party (capped at MaxSlotsPerParty), with optional pins.
    public static async Task<EditResult> AddSlotAsync(
        ApplicationDbContext db, int eventId, int partyId,
        string? role, string? mainJob, string? subJob, CancellationToken cancellationToken)
    {
        var ensure = await EnsureEventOwnedSetupAsync(db, eventId, cancellationToken);
        if (ensure.SetupId is not { } setupId) { return NoSetup(); }
        partyId = Translate(ensure.PartyIdMap, partyId);

        var party = await db.PartySetupParties.Include(p => p.Slots)
            .FirstOrDefaultAsync(p => p.Id == partyId && p.Alliance!.PartySetupId == setupId, cancellationToken);
        if (party is null) { return new EditResult(false, "Party not found."); }
        if (party.Slots.Count >= MaxSlotsPerParty)
        {
            return new EditResult(false, $"A party can hold at most {MaxSlotsPerParty} slots.");
        }

        var (type, r, m, s) = DeriveRequirement(role, mainJob, subJob);
        party.Slots.Add(new PartySetupSlot
        {
            PartySetupPartyId = partyId,
            SortOrder = party.Slots.Count,
            RequirementType = type,
            Role = r,
            MainJob = m,
            SubJob = s,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new EditResult(true, null);
    }

    // Delete a slot. Any occupant is moved to "Also Attending" BEFORE removal (so the
    // cascade-delete of the slot signup can't wipe their attendance/DKP).
    public static async Task<EditResult> DeleteSlotAsync(
        ApplicationDbContext db, int eventId, int slotId, CancellationToken cancellationToken)
    {
        var ensure = await EnsureEventOwnedSetupAsync(db, eventId, cancellationToken);
        if (ensure.SetupId is not { } setupId) { return NoSetup(); }
        slotId = Translate(ensure.SlotIdMap, slotId);

        var ev = await db.Events.FirstAsync(e => e.Id == eventId, cancellationToken);
        var slot = await LoadSlotAsync(db, setupId, slotId, cancellationToken);
        if (slot is null) { return new EditResult(false, "Slot not found."); }
        var partyId = slot.PartySetupPartyId;

        var occupant = await db.EventPartySlotSignups
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.PartySetupSlotId == slot.Id, cancellationToken);
        if (occupant is not null)
        {
            await EventPartySignupService.MoveSlotSignupToNoSlotAsync(
                db, eventId, occupant, ev.CommencementStartTime ?? ev.StartTime, cancellationToken);
        }
        db.PartySetupSlots.Remove(slot);
        await db.SaveChangesAsync(cancellationToken);

        var remaining = await db.PartySetupSlots
            .Where(s => s.PartySetupPartyId == partyId).OrderBy(s => s.SortOrder).ToListAsync(cancellationToken);
        for (var i = 0; i < remaining.Count; i++) { remaining[i].SortOrder = i; }
        await db.SaveChangesAsync(cancellationToken);
        return new EditResult(true, null);
    }

    // Add a party to an alliance (capped at MaxPartiesPerAlliance).
    public static async Task<EditResult> AddPartyAsync(
        ApplicationDbContext db, int eventId, int allianceId, string? name, CancellationToken cancellationToken)
    {
        var ensure = await EnsureEventOwnedSetupAsync(db, eventId, cancellationToken);
        if (ensure.SetupId is not { } setupId) { return NoSetup(); }
        allianceId = Translate(ensure.AllianceIdMap, allianceId);

        var alliance = await db.PartySetupAlliances.Include(a => a.Parties)
            .FirstOrDefaultAsync(a => a.Id == allianceId && a.PartySetupId == setupId, cancellationToken);
        if (alliance is null) { return new EditResult(false, "Alliance not found."); }
        if (alliance.Parties.Count >= MaxPartiesPerAlliance)
        {
            return new EditResult(false, $"An alliance can hold at most {MaxPartiesPerAlliance} parties.");
        }

        alliance.Parties.Add(new PartySetupParty
        {
            PartySetupAllianceId = allianceId,
            SortOrder = alliance.Parties.Count,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
        });
        await db.SaveChangesAsync(cancellationToken);
        return new EditResult(true, null);
    }

    // Remove a party (alliance keeps at least one). All its slot occupants are moved to
    // "Also Attending" first so nobody is wiped by the cascade.
    public static async Task<EditResult> RemovePartyAsync(
        ApplicationDbContext db, int eventId, int partyId, CancellationToken cancellationToken)
    {
        var ensure = await EnsureEventOwnedSetupAsync(db, eventId, cancellationToken);
        if (ensure.SetupId is not { } setupId) { return NoSetup(); }
        partyId = Translate(ensure.PartyIdMap, partyId);

        var ev = await db.Events.FirstAsync(e => e.Id == eventId, cancellationToken);
        var party = await db.PartySetupParties.Include(p => p.Slots)
            .FirstOrDefaultAsync(p => p.Id == partyId && p.Alliance!.PartySetupId == setupId, cancellationToken);
        if (party is null) { return new EditResult(false, "Party not found."); }
        var allianceId = party.PartySetupAllianceId;
        var partyCount = await db.PartySetupParties.CountAsync(p => p.PartySetupAllianceId == allianceId, cancellationToken);
        if (partyCount <= 1) { return new EditResult(false, "An alliance must keep at least one party."); }

        var slotIds = party.Slots.Select(s => s.Id).ToList();
        var occupants = await db.EventPartySlotSignups
            .Where(s => s.EventId == eventId && slotIds.Contains(s.PartySetupSlotId)).ToListAsync(cancellationToken);
        foreach (var occ in occupants)
        {
            await EventPartySignupService.MoveSlotSignupToNoSlotAsync(
                db, eventId, occ, ev.CommencementStartTime ?? ev.StartTime, cancellationToken);
        }
        db.PartySetupParties.Remove(party);
        await db.SaveChangesAsync(cancellationToken);

        var remaining = await db.PartySetupParties
            .Where(p => p.PartySetupAllianceId == allianceId).OrderBy(p => p.SortOrder).ToListAsync(cancellationToken);
        for (var i = 0; i < remaining.Count; i++) { remaining[i].SortOrder = i; }
        await db.SaveChangesAsync(cancellationToken);
        return new EditResult(true, null);
    }

    // Rename an alliance or a party (pass exactly one id).
    public static async Task<EditResult> RenameAsync(
        ApplicationDbContext db, int eventId, int? allianceId, int? partyId, string? name, CancellationToken cancellationToken)
    {
        var ensure = await EnsureEventOwnedSetupAsync(db, eventId, cancellationToken);
        if (ensure.SetupId is not { } setupId) { return NoSetup(); }

        var newName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (newName is { Length: > 64 }) { newName = newName[..64]; }

        if (partyId is { } pid)
        {
            pid = Translate(ensure.PartyIdMap, pid);
            var party = await db.PartySetupParties
                .FirstOrDefaultAsync(p => p.Id == pid && p.Alliance!.PartySetupId == setupId, cancellationToken);
            if (party is null) { return new EditResult(false, "Party not found."); }
            party.Name = newName;
        }
        else if (allianceId is { } aid)
        {
            aid = Translate(ensure.AllianceIdMap, aid);
            var alliance = await db.PartySetupAlliances
                .FirstOrDefaultAsync(a => a.Id == aid && a.PartySetupId == setupId, cancellationToken);
            if (alliance is null) { return new EditResult(false, "Alliance not found."); }
            alliance.Name = newName ?? string.Empty; // PartySetupAlliance.Name is non-nullable (blank => default label)
        }
        else
        {
            return new EditResult(false, "Nothing to rename.");
        }
        await db.SaveChangesAsync(cancellationToken);
        return new EditResult(true, null);
    }

    // --- helpers ----------------------------------------------------------------------

    private static EditResult NoSetup() => new(false, "This event has no party setup to edit.");

    private static int Translate(IReadOnlyDictionary<int, int> map, int id)
        => map.TryGetValue(id, out var mapped) ? mapped : id;

    private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static Task<PartySetupSlot?> LoadSlotAsync(ApplicationDbContext db, int setupId, int slotId, CancellationToken ct)
        => db.PartySetupSlots
            .FirstOrDefaultAsync(s => s.Id == slotId && s.Party!.Alliance!.PartySetupId == setupId, ct);

    private static void DetachCloneSubtree(ApplicationDbContext db, PartySetup clone)
    {
        foreach (var slot in clone.Alliances.SelectMany(a => a.Parties).SelectMany(p => p.Slots))
        {
            db.Entry(slot).State = EntityState.Detached;
        }
        foreach (var party in clone.Alliances.SelectMany(a => a.Parties))
        {
            db.Entry(party).State = EntityState.Detached;
        }
        foreach (var alliance in clone.Alliances)
        {
            db.Entry(alliance).State = EntityState.Detached;
        }
        db.Entry(clone).State = EntityState.Detached;
    }

    // RequirementType is derived by precedence (Job > Role > Any); "Any Role" is the
    // editor's "anything goes" sentinel and stores as an open Any slot. Mirrors
    // ActivityDataController.MapPartySlot / PartySetupController.MapSlot.
    private static (string Type, string? Role, string? Main, string? Sub) DeriveRequirement(
        string? role, string? mainJob, string? subJob)
    {
        role = role?.Trim();
        mainJob = mainJob?.Trim();
        subJob = subJob?.Trim();
        if (string.Equals(role, "Any Role", StringComparison.OrdinalIgnoreCase)) { role = null; }

        var hasMain = !string.IsNullOrWhiteSpace(mainJob) && ValidMainJobs.Contains(mainJob!);
        var hasRole = !string.IsNullOrWhiteSpace(role) && ValidRoles.Contains(role!);

        if (hasMain)
        {
            var sub = (!string.IsNullOrWhiteSpace(subJob) && ValidSubJobs.Contains(subJob!)) ? subJob : null;
            return (ReqJob, hasRole ? role : null, mainJob, sub);
        }
        if (hasRole)
        {
            return (ReqRole, role, null, null);
        }
        return (ReqAny, null, null, null);
    }
}
