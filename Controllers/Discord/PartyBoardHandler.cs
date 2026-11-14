using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static LinkshellManagerDiscordApp.Controllers.Discord.DiscordSignupIdentity;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// Sign-ups and the party board: the job select and Withdraw on a plain event, the party board's
// Sign Up / Sign Up as Party Leader drill-down (alliance -> party -> slot -> job wizard), Withdraw,
// Make Me Party / Alliance Lead, My Readiness, and "Sign Up (No Slot)" with its job wizard.
//
// The slot claim and job wizard also seat a member for an officer (officerAdd: true); the officer
// tools call them for that.
public sealed class PartyBoardHandler : DiscordInteractionHandlerBase
{
    private readonly ApplicationDbContext _db;
    private readonly DiscordEventChannelQueue _eventQueue;
    private readonly OfficerAddTargetCache _officerAddTargets;
    private readonly DiscordSignupIdentity _identity;
    private readonly DiscordPermissionGates _gates;
    private readonly DiscordBoardSupport _board;

    public PartyBoardHandler(
        DiscordInteractionContext interaction,
        ApplicationDbContext db,
        DiscordEventChannelQueue eventQueue,
        OfficerAddTargetCache officerAddTargets,
        DiscordSignupIdentity identity,
        DiscordPermissionGates gates,
        DiscordBoardSupport board)
        : base(interaction)
    {
        _db = db;
        _eventQueue = eventQueue;
        _officerAddTargets = officerAddTargets;
        _identity = identity;
        _gates = gates;
        _board = board;
    }

    internal Task<IActionResult> HandleJobSignupAsync(
        int eventId, string? appUserId, string? job, CancellationToken cancellationToken) =>
        _board.RetryIfParticipationRaceAsync(() => HandleJobSignupOnceAsync(eventId, appUserId, job, cancellationToken));

    private async Task<IActionResult> HandleJobSignupOnceAsync(
        int eventId, string? appUserId, string? job, CancellationToken cancellationToken)
    {
        if (eventId <= 0 || string.IsNullOrWhiteSpace(job))
        {
            return Ephemeral("Pick a job to sign up.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        var ctx = await _identity.ResolveSignupContextAsync(ev, appUserId, $"job:{eventId}:{job}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }
        var characterName = ctx.CharacterName!;

        // Signing up again switches jobs IN PLACE so accrued time (StartTime /
        // Duration / break state) is preserved instead of restarting the clock.
        var existing = ctx.AppUserId is not null
            ? await _db.AppUserEvents.FirstOrDefaultAsync(
                item => item.EventId == eventId && item.AppUserId == ctx.AppUserId, cancellationToken)
            : await _db.AppUserEvents.FirstOrDefaultAsync(
                item => item.EventId == eventId && item.AppUserId == null && item.DiscordUserId == ctx.DiscordUserId, cancellationToken);
        if (existing is not null)
        {
            existing.CharacterName = characterName;
            existing.JobName = job!.Trim();
        }
        else
        {
            // A sign-up on a live event starts its clock now, not at the event start.
            var (startTime, isQuickJoin) = SignupClock.ForNewSignup(ev, DateTime.UtcNow);
            _db.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = ctx.AppUserId,
                DiscordUserId = ctx.DiscordUserId,
                EventId = eventId,
                CharacterName = characterName,
                JobName = job!.Trim(),
                StartTime = startTime,
                IsQuickJoin = isQuickJoin
            });
        }
        await _db.SaveChangesAsync(cancellationToken);

        return await _board.UpdatedEventMessageAsync(ev.Id, cancellationToken);
    }

    internal async Task<IActionResult> HandleWithdrawAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        var identity = await _identity.ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }

        var existing = identity.Value.AppUserId is not null
            ? await _db.AppUserEvents.FirstOrDefaultAsync(
                item => item.EventId == eventId && item.AppUserId == identity.Value.AppUserId, cancellationToken)
            // Outside clicker: match by Discord id ALONE (not also AppUserId == null) so
            // it also finds a placeholder-matched row, which carries a non-null AppUserId.
            : await _db.AppUserEvents.FirstOrDefaultAsync(
                item => item.EventId == eventId && item.DiscordUserId == identity.Value.DiscordUserId, cancellationToken);
        if (existing is not null)
        {
            _db.AppUserEvents.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }

        _identity.ClearCharacterChoice(identity.Value.AppUserId, identity.Value.DiscordUserId, eventId);
        return await _board.UpdatedEventMessageAsync(ev.Id, cancellationToken);
    }

    // The signer a slot claim / job wizard should be attributed to. For a normal signup
    // that's the clicker (ResolveSignupContextAsync). For an officer-add it's the cached
    // TARGET member — the clicker (the officer) is re-verified, then their cached target is
    // returned (keyed by the officer's Discord id + event). The target always has an
    // AppUserId; its Discord id is carried only for placeholders (so they can still
    // self-withdraw), which PlaceholderMatch dual-stamps onto the claim.
    private async Task<SignupContext> ResolveSignerForClaimAsync(
        Event ev, string? clickerAppUserId, bool officerAdd, string promptTail, CancellationToken cancellationToken)
    {
        if (!officerAdd)
        {
            return await _identity.ResolveSignupContextAsync(ev, clickerAppUserId, promptTail, cancellationToken);
        }
        if (!await _gates.CanEditPartyBoardAsync(ev, clickerAppUserId, cancellationToken))
        {
            return SignupContext.Stop(Ephemeral("You need the **Manage parties** permission to add members to the board."));
        }
        if (string.IsNullOrEmpty(Interaction.DiscordUserId))
        {
            return SignupContext.Stop(Ephemeral("Couldn't read your Discord account from that click."));
        }
        var target = _officerAddTargets.Peek(Interaction.DiscordUserId, ev.Id);
        if (target is null)
        {
            return SignupContext.Stop(Ephemeral("That add-member session expired — tap **➕ Add Member** again."));
        }
        return string.IsNullOrEmpty(target.DiscordUserId)
            ? SignupContext.Account(target.AppUserId, target.CharacterName)
            : SignupContext.PlaceholderMatch(target.AppUserId, target.DiscordUserId, target.CharacterName);
    }

    // "Sign Up" / "Sign Up as Party Leader" on the board → the ephemeral drill-down:
    // Alliance → Party → Slot, each a select that morphs the previous in place. Single-
    // choice levels are skipped so you never pick from a list of one. The final slot pick
    // runs HandlePartySlotClaimAsync (job wizard + claim), unchanged. The leader path
    // restricts every level to leaderless parties.
    internal async Task<IActionResult> HandlePartySlotSignUpAsync(
        int eventId, string? appUserId, bool asLeader, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _board.LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        // Resolve identity (and prompt the alt picker / outside-name modal if needed)
        // before showing the picker — the claim itself re-resolves the same way.
        var ctx = await _identity.ResolveSignupContextAsync(ev, appUserId, $"{(asLeader ? "slotL" : "slot")}:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);

        // Start the drill-down at the alliance step (skipping it when only one alliance
        // has an opening — then the party step, likewise skipped when only one party has one).
        var alliances = ev.PartySetup.Alliances.OrderBy(a => a.SortOrder).ToList();
        var openAllianceIndexes = Enumerable.Range(0, alliances.Count)
            .Where(i => AllianceHasOpening(alliances[i], slotSignups, asLeader))
            .ToList();
        if (openAllianceIndexes.Count == 0)
        {
            return Ephemeral(asLeader
                ? "There's no party to lead right now — every party already has a leader (or has no open slots)."
                : "Every slot is taken right now.");
        }
        if (openAllianceIndexes.Count == 1)
        {
            return ShowPartyStep(ev, slotSignups, openAllianceIndexes[0], asLeader);
        }

        var allianceRows = DiscordEventMessageBuilder.BuildAlliancePickerComponents(eventId, ev.PartySetup, slotSignups, asLeader);
        return PickerResponse(
            EventHeading(ev.EventName, asLeader ? "Pick an alliance to lead in:" : "Pick an alliance:"),
            allianceRows);
    }

    // Drill-down step 1 → 2: the member chose an alliance (value = its SortOrder index).
    internal async Task<IActionResult> HandleAlliancePickedAsync(
        int eventId, int allianceIndex, bool asLeader, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }
        var ev = await _board.LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        if (allianceIndex < 0 || allianceIndex >= ev.PartySetup.Alliances.Count)
        {
            return Ephemeral("That alliance is no longer available.");
        }
        return ShowPartyStep(ev, slotSignups, allianceIndex, asLeader);
    }

    // Drill-down step 2 → 3: the member chose a party (value = party id).
    internal async Task<IActionResult> HandlePartyPickedAsync(
        int eventId, int partyId, bool asLeader, CancellationToken cancellationToken)
    {
        if (eventId <= 0 || partyId <= 0)
        {
            return Ephemeral("That party isn't recognized.");
        }
        var ev = await _board.LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        return ShowSlotStep(ev, slotSignups, partyId, asLeader);
    }

    // Show the party picker for one alliance, or skip straight to the slot step when that
    // alliance has just one party with an opening.
    private IActionResult ShowPartyStep(
        Event ev, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, int allianceIndex, bool asLeader)
    {
        var alliance = ev.PartySetup!.Alliances.OrderBy(a => a.SortOrder).ElementAt(allianceIndex);
        var openParties = alliance.Parties.OrderBy(p => p.SortOrder)
            .Where(p => PartyHasOpening(p, slotSignups, asLeader))
            .ToList();
        if (openParties.Count == 0)
        {
            // Reached only on a race (parties filled between clicks) → morph the picker to
            // the notice in place (PickerResponse falls back to a fresh ephemeral on a
            // board-click source, so it never edits the public board).
            return PickerResponse(
                asLeader ? "That alliance has no party to lead right now." : "That alliance is full right now.",
                Array.Empty<object>());
        }
        if (openParties.Count == 1)
        {
            return ShowSlotStep(ev, slotSignups, openParties[0].Id, asLeader);
        }
        var rows = DiscordEventMessageBuilder.BuildPartyPickerComponents(ev.Id, ev.PartySetup, slotSignups, allianceIndex, asLeader);
        return PickerResponse(
            EventHeading(ev.EventName, asLeader ? "Pick a party to lead:" : "Pick a party:"),
            rows);
    }

    // Show the open-slot picker for one party (the terminal drill-down step; picking a
    // slot routes to the existing claim + job wizard).
    private IActionResult ShowSlotStep(
        Event ev, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, int partyId, bool asLeader)
    {
        var rows = DiscordEventMessageBuilder.BuildPartySlotPickerComponents(ev.Id, ev.PartySetup!, slotSignups, partyId, asLeader);
        if (rows.Length == 0)
        {
            // Race: slots filled between clicks → morph in place (fresh ephemeral on a
            // board-click source).
            return PickerResponse("Every slot in that party was just taken. Tap Sign Up again to pick another.", Array.Empty<object>());
        }
        return PickerResponse(
            EventHeading(ev.EventName, asLeader ? "Pick a slot to claim as party leader 👑:" : "Pick a slot to claim:"),
            rows);
    }

    // An alliance/party still has an opening for this flow (leader: a leaderless party
    // with an open slot; regular: any open slot).
    private static bool AllianceHasOpening(
        PartySetupAlliance alliance, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, bool asLeader)
        => alliance.Parties.Any(p => PartyHasOpening(p, slotSignups, asLeader));

    private static bool PartyHasOpening(
        PartySetupParty party, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, bool asLeader)
    {
        if (asLeader && party.Slots.Any(s => slotSignups.TryGetValue(s.Id, out var su) && su.IsPartyLeader))
        {
            return false; // leader flow: skip parties that already have a leader
        }
        return party.Slots.Any(s => !slotSignups.ContainsKey(s.Id));
    }

    // Picker select → claim the chosen slot for THIS event. If the slot pins both
    // a role and a main job, claim immediately; otherwise open a modal to collect
    // the missing job pick(s) (the claim then happens on modal submit).
    internal async Task<IActionResult> HandlePartySlotClaimAsync(
        int eventId, int slotId, string? appUserId, bool asLeader, CancellationToken cancellationToken,
        bool officerAdd = false)
    {
        if (eventId <= 0 || slotId <= 0)
        {
            return Ephemeral("That slot isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null || ev.PartySetupId is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        var slot = await _db.PartySetupSlots
            .Include(s => s.Party!).ThenInclude(p => p.Alliance!)
            .FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken);
        if (slot is null || slot.Party?.Alliance?.PartySetupId != ev.PartySetupId)
        {
            return Ephemeral("That slot isn't part of this event.");
        }

        // Needs a job pick whenever a required field isn't pinned → start the
        // ephemeral dropdown wizard (role → main → sub). An "ANY" sub
        // (slot pins the main but leaves the sub open) counts too: the member must
        // still specify which sub they're bringing, so an empty SubJob enters the
        // wizard and the pinned role/main steps are simply skipped inside it.
        if (string.IsNullOrWhiteSpace(slot.Role)
            || string.IsNullOrWhiteSpace(slot.MainJob)
            || string.IsNullOrWhiteSpace(slot.SubJob))
        {
            return await AdvancePartyJobWizardAsync(eventId, slotId, null, null, null, false, appUserId, asLeader, cancellationToken, officerAdd);
        }

        var ctx = await ResolveSignerForClaimAsync(ev, appUserId, officerAdd, $"{(asLeader ? "slotL" : "slot")}:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        var result = await EventPartySignupService.ClaimSlotAsync(
            _db, eventId, slot, ctx.AppUserId, ctx.CharacterName!, null, null, null, cancellationToken, asLeader,
            discordUserId: ctx.DiscordUserId);
        if (!result.Success)
        {
            return Ephemeral(result.Error ?? "Couldn't claim that slot.");
        }
        if (!await TryCommitSlotClaimAsync(cancellationToken))
        {
            return Ephemeral("That slot was just taken by another member. Pick another open slot.");
        }
        // Pre-start: drop their no-slot attendance. Live: materialize the claim as a
        // participation so a late joiner lands in the running event immediately.
        await EventPartySignupService.SyncParticipationAfterClaimAsync(_db, ev, ctx.AppUserId, cancellationToken, ctx.DiscordUserId);
        await _db.SaveChangesAsync(cancellationToken);
        if (officerAdd) { ClearOfficerAddTarget(eventId); }

        // The select lives on the ephemeral picker; queue the board refresh (the
        // image render runs off the 3s window) and silently dismiss the picker.
        _eventQueue.Enqueue(eventId);
        return _board.DismissPickerSilently();
    }

    // Drives the job-pick wizard: presents the next needed dropdown (role → main →
    // sub) as an ephemeral message update, carrying the picks made so far in the
    // select custom_ids; once everything needed is gathered, claims the slot, edits
    // the board, and confirms. role/main/sub are the picks so far (null = not yet
    // picked, or pinned by the slot).
    internal async Task<IActionResult> AdvancePartyJobWizardAsync(
        int eventId, int slotId, string? role, string? main, string? sub, bool subPicked,
        string? appUserId, bool asLeader, CancellationToken cancellationToken, bool officerAdd = false)
    {
        if (eventId <= 0 || slotId <= 0)
        {
            return Ephemeral("That slot isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null || ev.PartySetupId is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        var slot = await _db.PartySetupSlots
            .Include(s => s.Party!).ThenInclude(p => p.Alliance!)
            .FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken);
        if (slot is null || slot.Party?.Alliance?.PartySetupId != ev.PartySetupId)
        {
            return Ephemeral("That slot isn't part of this event.");
        }

        // Resolve identity once (the name was cached at the signup entry, so this
        // won't re-prompt). For an officer-add this is the cached TARGET member, not the
        // clicking officer. Used at the final claim below.
        var ctx = await ResolveSignerForClaimAsync(ev, appUserId, officerAdd, $"{(asLeader ? "slotL" : "slot")}:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        // Carry the flow's intent through the wizard via the matching prefix family (same
        // tail format): officer-add seats the cached target, the leader variant claims as
        // party leader, and the plain prefixes drive a normal self-signup. Officer-add never
        // also claims leadership (asLeader is forced false on that path).
        var rolePrefix = officerAdd ? DiscordEventMessageBuilder.OfficerAddWizardRolePrefix
            : asLeader ? DiscordEventMessageBuilder.PartyWizardLeaderRolePrefix : DiscordEventMessageBuilder.PartyWizardRolePrefix;
        var mainPrefix = officerAdd ? DiscordEventMessageBuilder.OfficerAddWizardMainPrefix
            : asLeader ? DiscordEventMessageBuilder.PartyWizardLeaderMainPrefix : DiscordEventMessageBuilder.PartyWizardMainPrefix;
        var subPrefix = officerAdd ? DiscordEventMessageBuilder.OfficerAddWizardSubPrefix
            : asLeader ? DiscordEventMessageBuilder.PartyWizardLeaderSubPrefix : DiscordEventMessageBuilder.PartyWizardSubPrefix;
        var leaderTag = asLeader ? " (as leader 👑)" : string.Empty;
        // Officer-add phrases the steps about the target ("Seat X" / "the main job") rather
        // than the clicker ("Sign up" / "your main job").
        var stepLead = officerAdd ? $"Seat {ctx.CharacterName}" : $"Sign up{leaderTag}";
        var possessive = officerAdd ? "the" : "your";

        // Present the next unpinned-and-not-yet-picked field as a dropdown.
        if (string.IsNullOrWhiteSpace(slot.Role) && string.IsNullOrWhiteSpace(role))
        {
            return WizardStep(
                EventHeading(ev.EventName, $"{stepLead} — {DiscordEventMessageBuilder.SlotRequirement(slot)}"),
                JobSelectRow(rolePrefix, $"{eventId}:{slotId}",
                    "Pick a role", EventJobCatalog.JobTypeOptions));
        }
        if (string.IsNullOrWhiteSpace(slot.MainJob) && string.IsNullOrWhiteSpace(main))
        {
            return WizardStep(
                EventHeading(ev.EventName, $"Pick {possessive} main job:"),
                JobSelectRow(mainPrefix, $"{eventId}:{slotId}:{role ?? "-"}",
                    $"Pick {possessive} main job", EventJobCatalog.MainJobOptions));
        }
        if (string.IsNullOrWhiteSpace(slot.SubJob) && !subPicked)
        {
            // A sub job is REQUIRED — no "no sub" option. Options exclude the
            // effective main (collected or pinned) so a member can't pick e.g. PLD/PLD.
            var effectiveMain = main ?? slot.MainJob;
            var subOptions = EventJobCatalog.SubJobOptions
                .Where(j => !string.Equals(j, effectiveMain, StringComparison.OrdinalIgnoreCase))
                .Select(j => (object)new { label = j, value = j })
                .ToArray();
            return WizardStep(
                EventHeading(ev.EventName, $"Pick {possessive} sub job:"),
                SelectRow(subPrefix, $"{eventId}:{slotId}:{role ?? "-"}:{main ?? "-"}",
                    $"Pick {possessive} sub job", subOptions));
        }

        // Everything needed is collected → claim, edit the board, confirm.
        var result = await EventPartySignupService.ClaimSlotAsync(
            _db, eventId, slot, ctx.AppUserId, ctx.CharacterName!,
            NormalizeWizardValue(role), NormalizeWizardValue(main), NormalizeWizardValue(sub), cancellationToken, asLeader,
            discordUserId: ctx.DiscordUserId);
        if (!result.Success)
        {
            return WizardStep($"⚠️ {result.Error}", Array.Empty<object>());
        }
        if (!await TryCommitSlotClaimAsync(cancellationToken))
        {
            return WizardStep("⚠️ That slot was just taken by another member. Pick another open slot.", Array.Empty<object>());
        }
        // Pre-start: drop their no-slot attendance. Live: materialize the claim as a
        // participation so a late joiner lands in the running event immediately.
        await EventPartySignupService.SyncParticipationAfterClaimAsync(_db, ev, ctx.AppUserId, cancellationToken, ctx.DiscordUserId);
        await _db.SaveChangesAsync(cancellationToken);
        if (officerAdd) { ClearOfficerAddTarget(eventId); }
        _eventQueue.Enqueue(eventId); // async board refresh (image render off the 3s window)
        return _board.DismissPickerSilently();
    }

    // Forget the officer's cached add target for this event (a successful seat completes the
    // flow; a stale target would otherwise mis-route a later officer-add wizard step).
    private void ClearOfficerAddTarget(int eventId)
    {
        if (!string.IsNullOrEmpty(Interaction.DiscordUserId)) { _officerAddTargets.Clear(Interaction.DiscordUserId, eventId); }
    }

    // Commits a pending slot claim. ClaimSlotAsync's check-then-insert is a
    // TOCTOU race: two members clicking the same open slot simultaneously can
    // both pass the in-memory "is it free?" check, and the second commit then
    // violates the unique (EventId, PartySetupSlotId) index. We translate that
    // one race into a friendly "taken" outcome (false) instead of letting the
    // DbUpdateException bubble to a 500 / Discord "interaction failed". Any other
    // update failure is a real fault and is allowed to propagate.
    private async Task<bool> TryCommitSlotClaimAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    // "Leave event" lives on the board itself, so refresh the board in place. Drops
    // BOTH the member's party slot (if any) AND their general attendance (if any).
    internal async Task<IActionResult> HandlePartySlotLeaveAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null || ev.PartySetupId is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        // Self-withdraw is allowed even after the event is live: many players only ever
        // use the Discord board (never the app), so they must be able to free their slot
        // and re-sign mid-run rather than waiting on an officer. The live Event row is
        // deleted when the event ends, so this is naturally bounded — a post-end click
        // finds no event and is rejected above. (The Activity path stays officer-gated;
        // see ActivityEventsController.WithdrawEventPartySlotAsync.)
        var identity = await _identity.ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }
        var (idAppUser, idDiscord) = identity.Value;
        var isLive = ev.CommencementStartTime is not null;

        var leftPartyId = await EventPartySignupService.LeaveAsync(_db, eventId, idAppUser, cancellationToken, idDiscord);

        // Before the event starts there's no DKP yet, so a withdrawal is a clean full
        // exit — also drop any "Join (no slot)" attendance they hold.
        //
        // A WINDOWED camp is a full exit too, live or not. Those credit attendance per posted
        // window rather than by time on the clock (EventBreakPolicy.IsWindowedAttendance — the same
        // test that refuses Break Room and mid-run withdraw on the app side), so there is no
        // accruing clock for the kept row to protect: keeping it only parked the member under
        // "Also Attending", which reads as still-signed-up to everyone looking at the board. On a
        // wyrm board it was doubly pointless, since the next window's roster clear deletes those
        // rows wholesale anyway (EventPartySignupService.ClearWindowRosterAsync).
        //
        // Everywhere else — a timed event paying by duration — the live case still KEEPS the
        // materialized participation: withdrawing from a slot must NOT wipe the event DKP they've
        // earned by attending. Only the party slot is freed, and they can re-sign into a new slot
        // with no DKP reset (the re-claim adopts the kept participation).
        var droppedAttendance = false;
        if (!isLive || EventBreakPolicy.IsWindowedAttendance(ev))
        {
            var attendance = idAppUser is not null
                ? await _db.AppUserEvents
                    .Where(p => p.EventId == eventId && p.AppUserId == idAppUser)
                    .ToListAsync(cancellationToken)
                // Outside clicker: match by Discord id ALONE so it also finds a
                // placeholder-matched row (which carries a non-null AppUserId).
                : await _db.AppUserEvents
                    .Where(p => p.EventId == eventId && p.DiscordUserId == idDiscord)
                    .ToListAsync(cancellationToken);
            if (attendance.Count > 0)
            {
                _db.AppUserEvents.RemoveRange(attendance);
                droppedAttendance = true;
            }
        }

        if (leftPartyId is null && !droppedAttendance)
        {
            // Leave lives on the shared board (the same button for everyone, which
            // Discord can't hide/grey per-user), so a click with nothing to free gets a
            // private notice instead of refreshing the board. On a duration-paid live event
            // that means they hold no slot but keep attendance/DKP — point them at an officer.
            // Windowed camps fall through to the plain "nothing to leave", because Withdraw
            // there removes everything and so leaves nothing behind to explain.
            return Ephemeral(isLive && !EventBreakPolicy.IsWindowedAttendance(ev)
                ? "You're attending this live event, so your DKP stays. Withdraw only frees a party slot — ask an officer if you need to be removed entirely."
                : "You're not signed up for this event, so there's nothing to leave.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        _identity.ClearCharacterChoice(idAppUser, idDiscord, eventId);
        // Queue the board refresh (it runs off the 3s
        // window) and silently acknowledge the click (no confirmation message); the
        // refreshed board is the feedback.
        _eventQueue.Enqueue(eventId);
        return Ok(new { type = ResponseDeferredUpdate });
    }

    // "Make Me Party Lead" → the pressing member, already in a party slot, takes that
    // party's leadership (👑), replacing whoever currently holds it. Like Withdraw,
    // it's a shared board button (Discord can't gate per-user), so the handler resolves
    // the clicker, finds their slot, and refuses with a private notice when there's
    // nothing to do. Leadership is purely a board designation (no perms), so it's
    // allowed before AND during a live event.
    internal async Task<IActionResult> HandleMakePartyLeaderAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null || ev.PartySetupId is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        // Identity resolves the same way as Withdraw: an account signup by AppUserId, a
        // board-only player by Discord id (no alt picker / name modal — they must
        // already hold a slot, so their identity is already on record).
        var identity = await _identity.ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }
        var (idAppUser, idDiscord) = identity.Value;

        var result = await EventPartySignupService.MakePartyLeaderAsync(_db, eventId, idAppUser, idDiscord, cancellationToken);
        if (!result.Success)
        {
            return Ephemeral(result.Error ?? "Couldn't make you the party leader.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        // Queue the board re-render (runs off the 3s window) and silently acknowledge —
        // the moved 👑 on the refreshed board is the feedback.
        _eventQueue.Enqueue(eventId);
        return Ok(new { type = ResponseDeferredUpdate });
    }

    // "Make Me Alliance Lead" → the pressing member, already in a party slot, takes their
    // whole ALLIANCE's lead (👑 by the alliance name), replacing whoever currently holds it.
    // Mirrors HandleMakePartyLeaderAsync one rung up: shared board button (Discord can't gate
    // per-user), so the handler resolves the clicker, finds their slot's alliance, and
    // refuses with a private notice when there's nothing to do. Purely a board designation
    // (no perms), so it's allowed before AND during a live event.
    internal async Task<IActionResult> HandleMakeAllianceLeaderAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null || ev.PartySetupId is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        var identity = await _identity.ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }
        var (idAppUser, idDiscord) = identity.Value;

        var result = await EventPartySignupService.MakeAllianceLeaderAsync(_db, eventId, idAppUser, idDiscord, cancellationToken);
        if (!result.Success)
        {
            return Ephemeral(result.Error ?? "Couldn't make you the alliance lead.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        // Queue the board re-render (runs off the 3s window) and silently acknowledge —
        // the moved 👑 on the refreshed board is the feedback.
        _eventQueue.Enqueue(eventId);
        return Ok(new { type = ResponseDeferredUpdate });
    }

    // "🎖️ My Readiness" → an ephemeral multi-select of the readiness tags (EventReadiness),
    // pre-ticked with what the clicker already carries. Shared board button (Discord can't gate
    // per-user), so like Withdraw / Make Me Party Lead the handler resolves the clicker and
    // refuses privately when they aren't on the board.
    //
    // A member declares this about THEMSELVES, so there's no officer gate here — the same
    // standing as picking the job they signed up with.
    internal async Task<IActionResult> HandleReadinessStartAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        var identity = await _identity.ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }
        var (idAppUser, idDiscord) = identity.Value;

        // Null = they hold no row on this board. Refuse here rather than letting them fill in
        // a picker whose save can only fail.
        var current = await EventPartySignupService.GetReadinessAsync(
            _db, eventId, idAppUser, idDiscord, cancellationToken);
        if (current is null)
        {
            return Ephemeral("You're not on this board yet — sign up first, then set your readiness.");
        }

        var (enfeeb, resist, relic) = current.Value;
        return PickerResponse(
            EventHeading(ev.EventName, "Set your readiness — tick everything you're bringing:"),
            DiscordEventMessageBuilder.BuildReadinessPickerComponents(eventId, enfeeb, resist, relic));
    }

    // The readiness picker's select → write the chosen tags onto the clicker's own rows.
    //
    // `picked` is the COMPLETE set the member still claims (the select is min_values:0), so an
    // empty list means "clear them all" — never "nothing was sent, leave things alone".
    internal async Task<IActionResult> HandleReadinessPickedAsync(
        int eventId, string? appUserId, IReadOnlyList<string> picked, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        var identity = await _identity.ResolveWithdrawIdentityAsync(ev, appUserId, cancellationToken);
        if (identity is null)
        {
            return Ephemeral("Open LSM and sign in with Discord once to link your account, then try again.");
        }
        var (idAppUser, idDiscord) = identity.Value;

        var chosen = new HashSet<string>(picked, StringComparer.Ordinal);
        var enfeeb = chosen.Contains(EventReadiness.Enfeeb);
        var resist = chosen.Contains(EventReadiness.Resist);
        var relic = chosen.Contains(EventReadiness.Relic);

        var saved = await EventPartySignupService.SetReadinessAsync(
            _db, eventId, idAppUser, idDiscord, enfeeb, resist, relic, cancellationToken);
        if (!saved)
        {
            return Ephemeral("You're not on this board yet — sign up first, then set your readiness.");
        }

        await _db.SaveChangesAsync(cancellationToken);
        _eventQueue.Enqueue(eventId); // re-render so the board picks the tags up

        // Morph the picker itself into the confirmation (no components) rather than stacking a
        // second ephemeral under it — the select has served its purpose once it's submitted.
        //
        // Deliberately says nothing about HOW the tags appear. They were once a spiked ring on a
        // rendered image, then coloured squares, then symbols beside the name. Promising a
        // particular rendering here just dates the message.
        var tags = EventReadiness.Selected(enfeeb, resist, relic);
        return WizardStep(
            tags.Count == 0
                ? "Readiness cleared."
                : "🎖️ Readiness set: "
                  + string.Join(", ", tags.Select(tag => $"{tag.Emoji} **{tag.Label}**")),
            Array.Empty<object>());
    }

    // "Join (no slot)" button on the board → a NEW ephemeral wizard message (so the
    // board itself isn't replaced). Subsequent picks morph this ephemeral via
    // AdvanceGeneralJoinWizardAsync. Starts at the role step (optional).
    internal async Task<IActionResult> StartGeneralJoinAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        // Gate + name prompt (the role/job picks come next; the name is cached now).
        var ctx = await _identity.ResolveSignupContextAsync(ev, appUserId, $"join:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        var roleOptions = new[] { (object)new { label = "No specific role", value = DiscordEventMessageBuilder.PartyWizardNoRole } }
            .Concat(EventJobCatalog.JobTypeOptions.Select(r => (object)new { label = r, value = r }))
            .ToArray();
        return PickerResponse(
            EventHeading(ev.EventName, $"{GeneralJoinTitle(ev)} — pick your role (optional):"),
            SelectRow(DiscordEventMessageBuilder.PartyJoinWizardRolePrefix, $"{eventId}", "Pick a role", roleOptions));
    }

    // What the general-join wizard calls itself: "Sign Up (No Slot)" beside a party board's slots,
    // plain "Sign Up" on the board for an event with no party setup, where there are no slots to
    // not take.
    private static string GeneralJoinTitle(Event ev) =>
        ev.PartySetupId is null ? "Sign Up" : "Sign Up (No Slot)";

    // "Join (no slot)" job-pick wizard: role (optional) → main job (required) →
    // sub (optional). Mirrors the slot wizard but there's no slot to claim — the
    // picks become a general-attendance AppUserEvent so the attendee always says
    // what job they're coming as (no more blank "Role Unassigned / Job/Sub" rows).
    // Re-running replaces the member's existing attendance for this event.
    internal Task<IActionResult> AdvanceGeneralJoinWizardAsync(
        int eventId, string? role, string? main, string? sub, bool subPicked,
        string? appUserId, CancellationToken cancellationToken) =>
        _board.RetryIfParticipationRaceAsync(() => AdvanceGeneralJoinWizardOnceAsync(
            eventId, role, main, sub, subPicked, appUserId, cancellationToken));

    private async Task<IActionResult> AdvanceGeneralJoinWizardOnceAsync(
        int eventId, string? role, string? main, string? sub, bool subPicked,
        string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }

        var ev = await _db.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        // Resolve identity once (the name was cached at the join entry) — used for
        // the attendance row written at the end of the wizard.
        var ctx = await _identity.ResolveSignupContextAsync(ev, appUserId, $"join:{eventId}", cancellationToken);
        if (ctx.ShouldStop)
        {
            return ctx.Interrupt!;
        }

        // Step 1 — role (optional; an explicit "No specific role" choice lets a
        // member proceed without one but still pick a job next).
        if (string.IsNullOrWhiteSpace(role))
        {
            var roleOptions = new[] { (object)new { label = "No specific role", value = DiscordEventMessageBuilder.PartyWizardNoRole } }
                .Concat(EventJobCatalog.JobTypeOptions.Select(r => (object)new { label = r, value = r }))
                .ToArray();
            return WizardStep(
                EventHeading(ev.EventName, $"{GeneralJoinTitle(ev)} — pick your role (optional):"),
                SelectRow(DiscordEventMessageBuilder.PartyJoinWizardRolePrefix, $"{eventId}", "Pick a role", roleOptions));
        }

        // Step 2 — main job (required).
        if (string.IsNullOrWhiteSpace(main))
        {
            return WizardStep(
                EventHeading(ev.EventName, "Pick your main job:"),
                JobSelectRow(DiscordEventMessageBuilder.PartyJoinWizardMainPrefix, $"{eventId}:{role}",
                    "Pick your main job", EventJobCatalog.MainJobOptions));
        }

        // Step 3 — sub job (optional). Exclude the chosen main + offer "no sub".
        if (!subPicked)
        {
            var subOptions = new[] { (object)new { label = "No sub job", value = DiscordEventMessageBuilder.PartyWizardNoSub } }
                .Concat(EventJobCatalog.SubJobOptions
                    .Where(j => !string.Equals(j, main, StringComparison.OrdinalIgnoreCase))
                    .Select(j => (object)new { label = j, value = j }))
                .ToArray();
            return WizardStep(
                EventHeading(ev.EventName, "Pick your sub job (optional):"),
                SelectRow(DiscordEventMessageBuilder.PartyJoinWizardSubPrefix, $"{eventId}:{role}:{main}",
                    "Pick your sub job", subOptions));
        }

        // Done — set the general-attendance row to the chosen job.
        var characterName = ctx.CharacterName!;

        var existing = ctx.AppUserId is not null
            ? await _db.AppUserEvents
                .Where(p => p.EventId == eventId && p.AppUserId == ctx.AppUserId)
                .ToListAsync(cancellationToken)
            : await _db.AppUserEvents
                .Where(p => p.EventId == eventId && p.AppUserId == null && p.DiscordUserId == ctx.DiscordUserId)
                .ToListAsync(cancellationToken);

        // One identity per event: joining "no slot" releases any party slot the
        // member currently holds, so they're never both in a slot and "no slot".
        var leftPartyId = await EventPartySignupService.LeaveAsync(_db, eventId, ctx.AppUserId, cancellationToken, ctx.DiscordUserId);

        if (existing.Count > 0)
        {
            // Switching jobs updates IN PLACE so accrued time (StartTime / Duration /
            // break state) is preserved; drop any duplicate rows.
            var keep = existing[0];
            keep.CharacterName = characterName;
            keep.JobType = NormalizeWizardValue(role);
            keep.JobName = NormalizeWizardValue(main);
            keep.SubJobName = NormalizeWizardValue(sub);
            for (var i = 1; i < existing.Count; i++) { _db.AppUserEvents.Remove(existing[i]); }
        }
        else
        {
            // A sign-up on a live event starts its clock now, not at the event start
            // (same rule as the app's /signup, see SignupClock).
            var (startTime, isQuickJoin) = SignupClock.ForNewSignup(ev, DateTime.UtcNow);
            _db.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = ctx.AppUserId,
                DiscordUserId = ctx.DiscordUserId,
                EventId = eventId,
                CharacterName = characterName,
                JobType = NormalizeWizardValue(role),
                JobName = NormalizeWizardValue(main),
                SubJobName = NormalizeWizardValue(sub),
                StartTime = startTime,
                IsQuickJoin = isQuickJoin,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
        _eventQueue.Enqueue(eventId); // async board refresh (image render off the 3s window)
        return _board.DismissPickerSilently();
    }
}
