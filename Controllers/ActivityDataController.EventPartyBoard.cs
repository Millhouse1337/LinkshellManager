using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinkshellManagerDiscordApp.Controllers;

// Per-EVENT party-setup board for the Activity. A party setup is a reusable
// template, so the roster is scoped to the event (EventPartySlotSignups) — these
// endpoints return/mutate that per-event roster, keeping the Activity panel in
// sync with the Discord board and the web event page. Reuses the same
// ActivityPartySetupDetailDto shape as the template board so the Angular panel
// renders it unchanged.
public sealed partial class ActivityDataController
{
    [HttpGet("events/{eventId:int}/party-board")]
    public async Task<IActionResult> GetEventPartyBoardAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to load the party board." });

        var ev = await _dbContext.Events
            .AsNoTracking()
            .Include(item => item.PartySetup!)
                .ThenInclude(ps => ps.Alliances).ThenInclude(a => a.Parties).ThenInclude(p => p.Slots)
            .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null) return NotFound(new { error = "Event party setup not found." });

        var membership = await GetMembershipAsync(appUser.Id, ev.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();

        var canManage = await CanAsync(membership, r => r.CanManageParties, cancellationToken);
        var signups = await EventPartySignupService.GetSignupsForEventAsync(_dbContext, eventId, cancellationToken);

        // "Also attending" = event participants who don't hold a party slot. Pre-start
        // these are the ad-hoc / "no slot" signups (slot holders live in
        // EventPartySlotSignups); once live, slot holders are materialized as
        // participations too, so filter them out by the slot-signup user ids.
        var slotUserIds = new HashSet<string>(
            signups.Values.Where(s => s.AppUserId != null).Select(s => s.AppUserId!),
            StringComparer.Ordinal);
        var participants = await _dbContext.AppUserEvents
            .AsNoTracking()
            .Where(p => p.EventId == eventId)
            .Select(p => new { p.AppUserId, p.CharacterName, p.JobType, p.JobName, p.SubJobName })
            .ToListAsync(cancellationToken);
        var alsoAttending = participants
            .Where(p => p.AppUserId == null || !slotUserIds.Contains(p.AppUserId))
            .OrderBy(p => p.CharacterName)
            .Select(p => new ActivityAlsoAttendingDto(p.CharacterName, p.JobType, p.JobName, p.SubJobName, p.AppUserId))
            .ToList();

        var setup = ev.PartySetup;
        var alliances = setup.Alliances
            .OrderBy(a => a.SortOrder)
            .Select(a =>
            {
                // The one signup in this alliance carrying the alliance-lead crown (if any).
                var allianceLead = a.Parties
                    .SelectMany(p => p.Slots)
                    .Select(s => signups.TryGetValue(s.Id, out var su) ? su : null)
                    .FirstOrDefault(su => su is { IsAllianceLeader: true });
                return new ActivityPartySetupAllianceDto(
                    a.Id,
                    string.IsNullOrWhiteSpace(a.Name) ? $"Alliance {a.SortOrder + 1}" : a.Name,
                    a.Parties
                        .OrderBy(p => p.SortOrder)
                        .Select(p => new ActivityPartySetupPartyDto(
                            p.Id,
                            string.IsNullOrWhiteSpace(p.Name) ? $"Party {p.SortOrder + 1}" : p.Name!,
                            p.Slots
                                .OrderBy(s => s.SortOrder)
                                .Select(s =>
                                {
                                    signups.TryGetValue(s.Id, out var su);
                                    return new ActivityPartySetupSlotDto(
                                        s.Id,
                                        s.SortOrder + 1,
                                        s.RequirementType,
                                        s.Role,
                                        s.MainJob,
                                        s.SubJob,
                                        s.Label,
                                        s.IsPartyLeader,
                                        su?.AppUserId,
                                        su?.CharacterName,
                                        su?.Role,
                                        su?.MainJob,
                                        su?.SubJob,
                                        su?.IsPartyLeader ?? false);
                                })
                                .ToList()))
                        .ToList(),
                    allianceLead?.AppUserId,
                    allianceLead?.CharacterName);
            })
            .ToList();

        return Ok(new ActivityPartySetupDetailDto(
            setup.Id, setup.LinkshellId, setup.Name, setup.EventType, setup.AssignedMonsterName, setup.Notes, canManage, alliances, alsoAttending));
    }

    [HttpPost("events/{eventId:int}/party-slots/{slotId:int}/signup")]
    public async Task<IActionResult> SignUpEventPartySlotAsync(
        int eventId,
        int slotId,
        [FromBody] ActivityPartySetupSignUpRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to sign up." });

        var ev = await _dbContext.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null || ev.PartySetupId is null) return NotFound(new { error = "Event party setup not found." });

        var slot = await _dbContext.PartySetupSlots
            .Include(s => s.Party!).ThenInclude(p => p.Alliance!)
            .FirstOrDefaultAsync(s => s.Id == slotId, cancellationToken);
        if (slot is null || slot.Party?.Alliance?.PartySetupId != ev.PartySetupId)
        {
            return NotFound(new { error = "Slot not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, ev.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();

        // "Fill earlier alliances first" nudge: if enabled and an open slot this member's
        // job can fill is still free in an EARLIER alliance, return the suggestion instead
        // of committing. The client offers it; "Sign up here anyway" re-posts with force.
        if (!request.Force)
        {
            var fillInOrder = await _dbContext.Linkshells
                .Where(l => l.Id == ev.LinkshellId)
                .Select(l => l.FillAlliancesInOrder)
                .FirstOrDefaultAsync(cancellationToken);
            if (fillInOrder)
            {
                var jobs = PartySetupSignupService.ResolveSignupJobs(slot, request.Role, request.MainJob, request.SubJob);
                if (jobs.Success)
                {
                    var setup = await _dbContext.PartySetups
                        .Include(ps => ps.Alliances).ThenInclude(a => a.Parties).ThenInclude(p => p.Slots)
                        .FirstOrDefaultAsync(ps => ps.Id == ev.PartySetupId.Value, cancellationToken);
                    if (setup is not null)
                    {
                        var signups = await EventPartySignupService.GetSignupsForEventAsync(_dbContext, eventId, cancellationToken);
                        var suggestion = PartyFillSuggestion.SuggestEarlierSlot(setup, signups, slot, jobs.Role, jobs.MainJob);
                        if (suggestion is not null && suggestion.Id != slot.Id)
                        {
                            return Ok(new
                            {
                                nudge = new
                                {
                                    suggestedSlotId = suggestion.Id,
                                    location = PartyFillSuggestion.DescribeSlot(setup, suggestion),
                                    requirement = PartyFillSuggestion.RequirementLabel(suggestion),
                                    role = jobs.Role,
                                    mainJob = jobs.MainJob,
                                    subJob = jobs.SubJob,
                                },
                            });
                        }
                    }
                }
            }
        }

        // Sign up as the member's main OR a chosen alt (validated against their
        // real characters; falls back to main if the requested name isn't theirs).
        var characterName = SignupCharacters.Resolve(appUser, membership, request.CharacterName);
        var result = await EventPartySignupService.ClaimSlotAsync(
            _dbContext, eventId, slot, appUser.Id, characterName, request.Role, request.MainJob, request.SubJob,
            cancellationToken, request.AsLeader);
        if (!result.Success) return BadRequest(new { error = result.Error });
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Lost the race: another member's claim for this slot committed first
            // and tripped the unique (EventId, PartySetupSlotId) index. Report it
            // as a conflict rather than a 500 so the client shows "slot taken".
            return Conflict(new { error = "That slot was just taken by another member. Pick another open slot." });
        }

        // Pre-start: drop their no-slot attendance. Live: materialize the claim as a
        // participation so a late joiner lands in the running event immediately.
        await EventPartySignupService.SyncParticipationAfterClaimAsync(_dbContext, ev, appUser.Id, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Auto-promote the earliest signup to leader if the party just filled
        // without anyone claiming leadership.
        await EventPartySignupService.ResolvePartyLeadershipAsync(
            _dbContext, eventId, slot.PartySetupPartyId, cancellationToken);
        EnqueueEventBoardRefresh(eventId);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/party-slots/{slotId:int}/withdraw")]
    public async Task<IActionResult> WithdrawEventPartySlotAsync(
        int eventId, int slotId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to withdraw." });

        var ev = await _dbContext.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null) return NotFound(new { error = "Event not found." });

        var membership = await GetMembershipAsync(appUser.Id, ev.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();

        var signup = await _dbContext.EventPartySlotSignups
            .FirstOrDefaultAsync(s => s.EventId == eventId && s.PartySetupSlotId == slotId, cancellationToken);
        if (signup is not null)
        {
            // The holder can drop their own slot; an officer with CanManageParties
            // can clear anyone's. Once the event is LIVE, only an officer can clear
            // a slot — members can't self-withdraw mid-run.
            var isOfficer = await CanAsync(membership, r => r.CanManageParties, cancellationToken);
            var isHolder = signup.AppUserId == appUser.Id;
            if (ev.CommencementStartTime is not null && !isOfficer)
            {
                return BadRequest(new { error = "The event is live — ask an officer to free your slot." });
            }
            if (!isHolder && !isOfficer)
            {
                return Forbid();
            }

            int? affectedPartyId;
            if (ev.CommencementStartTime is not null)
            {
                var startTime = ev.CommencementStartTime ?? ev.StartTime;
                affectedPartyId = await EventPartySignupService.MoveSlotSignupToNoSlotAsync(
                    _dbContext, eventId, signup, startTime, cancellationToken);
            }
            else
            {
                affectedPartyId = await _dbContext.PartySetupSlots
                    .Where(s => s.Id == signup.PartySetupSlotId)
                    .Select(s => (int?)s.PartySetupPartyId)
                    .FirstOrDefaultAsync(cancellationToken);
                _dbContext.EventPartySlotSignups.Remove(signup);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            await EventPartySignupService.ResolvePartyLeadershipAsync(_dbContext, eventId, affectedPartyId, cancellationToken);
            EnqueueEventBoardRefresh(eventId);
        }

        return Ok(new { success = true });
    }

    // "Make Me Alliance Lead": the calling member — who must already hold a slot in this
    // event — takes their whole alliance's lead (👑 by the alliance name), moving it off
    // whoever currently holds it. Mirrors the Discord/web "Make Me Alliance Lead" button.
    // Purely a board designation (no perms), allowed before AND during a live event.
    [HttpPost("events/{eventId:int}/make-alliance-lead")]
    public async Task<IActionResult> MakeEventAllianceLeadAsync(
        int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to take the lead." });

        var ev = await _dbContext.Events.FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (ev is null || ev.PartySetupId is null) return NotFound(new { error = "Event not found." });

        var membership = await GetMembershipAsync(appUser.Id, ev.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();

        var result = await EventPartySignupService.MakeAllianceLeaderAsync(
            _dbContext, eventId, appUser.Id, null, cancellationToken);
        if (!result.Success) return BadRequest(new { error = result.Error });

        await _dbContext.SaveChangesAsync(cancellationToken);
        EnqueueEventBoardRefresh(eventId);
        return Ok(new { success = true });
    }

    // --- Officer board editing (drag-drop + per-slot job changes) ---------------------
    // All gated by CanManageParties. Each delegates to EventPartyBoardEditService (which
    // lazily clones the shared template into a per-event snapshot on first edit, so edits
    // never touch the template/other events), then refreshes the Discord board. Responses
    // are { success } — the client reloads the board via GET to render authoritative state
    // (e.g. a displaced occupant appearing in "Also Attending").

    [HttpPost("events/{eventId:int}/board/slots/{slotId:int}/requirement")]
    public async Task<IActionResult> EditEventBoardSlotRequirementAsync(
        int eventId, int slotId, [FromBody] EditSlotRequirementRequest request, CancellationToken cancellationToken)
    {
        var guard = await GuardBoardEditAsync(eventId, cancellationToken);
        if (guard is not null) return guard;
        var result = await EventPartyBoardEditService.ChangeSlotRequirementAsync(
            _dbContext, eventId, slotId, request.Role, request.MainJob, request.SubJob, cancellationToken);
        return await BoardEditResult(eventId, result, cancellationToken);
    }

    [HttpPost("events/{eventId:int}/board/slots/{slotId:int}/move")]
    public async Task<IActionResult> MoveEventBoardSlotAsync(
        int eventId, int slotId, [FromBody] MoveSlotRequest request, CancellationToken cancellationToken)
    {
        var guard = await GuardBoardEditAsync(eventId, cancellationToken);
        if (guard is not null) return guard;
        var result = await EventPartyBoardEditService.MoveSlotAsync(
            _dbContext, eventId, slotId, request.TargetPartyId, request.TargetIndex, cancellationToken);
        return await BoardEditResult(eventId, result, cancellationToken);
    }

    [HttpPost("events/{eventId:int}/board/members/move")]
    public async Task<IActionResult> MoveEventBoardMemberAsync(
        int eventId, [FromBody] MoveMemberRequest request, CancellationToken cancellationToken)
    {
        var guard = await GuardBoardEditAsync(eventId, cancellationToken);
        if (guard is not null) return guard;
        var result = await EventPartyBoardEditService.MoveMemberAsync(
            _dbContext, eventId, request.FromSlotId, request.ToSlotId, request.AppUserId, request.DiscordUserId, cancellationToken);
        return await BoardEditResult(eventId, result, cancellationToken);
    }

    [HttpPost("events/{eventId:int}/board/slots")]
    public async Task<IActionResult> AddEventBoardSlotAsync(
        int eventId, [FromBody] AddSlotRequest request, CancellationToken cancellationToken)
    {
        var guard = await GuardBoardEditAsync(eventId, cancellationToken);
        if (guard is not null) return guard;
        var result = await EventPartyBoardEditService.AddSlotAsync(
            _dbContext, eventId, request.PartyId, request.Role, request.MainJob, request.SubJob, cancellationToken);
        return await BoardEditResult(eventId, result, cancellationToken);
    }

    [HttpPost("events/{eventId:int}/board/slots/{slotId:int}/delete")]
    public async Task<IActionResult> DeleteEventBoardSlotAsync(
        int eventId, int slotId, CancellationToken cancellationToken)
    {
        var guard = await GuardBoardEditAsync(eventId, cancellationToken);
        if (guard is not null) return guard;
        var result = await EventPartyBoardEditService.DeleteSlotAsync(_dbContext, eventId, slotId, cancellationToken);
        return await BoardEditResult(eventId, result, cancellationToken);
    }

    [HttpPost("events/{eventId:int}/board/parties")]
    public async Task<IActionResult> AddEventBoardPartyAsync(
        int eventId, [FromBody] AddPartyRequest request, CancellationToken cancellationToken)
    {
        var guard = await GuardBoardEditAsync(eventId, cancellationToken);
        if (guard is not null) return guard;
        var result = await EventPartyBoardEditService.AddPartyAsync(_dbContext, eventId, request.AllianceId, request.Name, cancellationToken);
        return await BoardEditResult(eventId, result, cancellationToken);
    }

    [HttpPost("events/{eventId:int}/board/parties/{partyId:int}/delete")]
    public async Task<IActionResult> RemoveEventBoardPartyAsync(
        int eventId, int partyId, CancellationToken cancellationToken)
    {
        var guard = await GuardBoardEditAsync(eventId, cancellationToken);
        if (guard is not null) return guard;
        var result = await EventPartyBoardEditService.RemovePartyAsync(_dbContext, eventId, partyId, cancellationToken);
        return await BoardEditResult(eventId, result, cancellationToken);
    }

    [HttpPost("events/{eventId:int}/board/rename")]
    public async Task<IActionResult> RenameEventBoardAsync(
        int eventId, [FromBody] RenameBoardRequest request, CancellationToken cancellationToken)
    {
        var guard = await GuardBoardEditAsync(eventId, cancellationToken);
        if (guard is not null) return guard;
        var result = await EventPartyBoardEditService.RenameAsync(
            _dbContext, eventId, request.AllianceId, request.PartyId, request.Name, cancellationToken);
        return await BoardEditResult(eventId, result, cancellationToken);
    }

    // Returns the error IActionResult to short-circuit a board edit, or null when the
    // caller (an officer with CanManageParties on the event's linkshell) may proceed.
    private async Task<IActionResult?> GuardBoardEditAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to edit the board." });
        var ev = await _dbContext.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (ev is null) return NotFound(new { error = "Event not found." });
        var membership = await GetMembershipAsync(appUser.Id, ev.LinkshellId, cancellationToken);
        if (membership is null) return Forbid();
        if (!await CanAsync(membership, r => r.CanManageParties, cancellationToken)) return Forbid();
        return null;
    }

    private async Task<IActionResult> BoardEditResult(
        int eventId, EventPartyBoardEditService.EditResult result, CancellationToken cancellationToken)
    {
        if (!result.Success) return BadRequest(new { error = result.Error });
        EnqueueEventBoardRefresh(eventId);
        // Push to the live change feed so other viewers (Activity + web long-poll)
        // refresh instantly. Member-affecting edits already notify via the
        // EventPartySlotSignup save hook; this covers structural edits (empty-slot job
        // change, add/delete slot, add/remove party, rename) that only touch PartySetup* rows.
        var linkshellId = await _dbContext.Events
            .Where(e => e.Id == eventId)
            .Select(e => e.LinkshellId)
            .FirstOrDefaultAsync(cancellationToken);
        if (linkshellId > 0)
        {
            HttpContext.RequestServices.GetRequiredService<LinkshellChangeNotifier>()
                .Notify(linkshellId, LinkshellChangeNotifier.Areas.Parties);
        }
        return Ok(new { success = true });
    }

    // Queues an async re-render of the event's posted Discord channel board so
    // signups / withdrawals made from the Activity (or web) show up in the message.
    // The DbContext auto-enqueue only fires for Event-entity add/edit, not for the
    // signup/participation rows a sign-up touches, so these paths enqueue directly.
    private void EnqueueEventBoardRefresh(int eventId)
    {
        HttpContext.RequestServices.GetService<DiscordEventChannelQueue>()?.Enqueue(eventId);
    }
}

// Officer board-edit request bodies (Activity + reused conceptually by the web JS).
public sealed record EditSlotRequirementRequest(string? Role, string? MainJob, string? SubJob);
public sealed record MoveSlotRequest(int TargetPartyId, int TargetIndex);
public sealed record MoveMemberRequest(int? FromSlotId, int? ToSlotId, string? AppUserId, string? DiscordUserId);
public sealed record AddSlotRequest(int PartyId, string? Role, string? MainJob, string? SubJob);
public sealed record AddPartyRequest(int AllianceId, string? Name);
public sealed record RenameBoardRequest(int? AllianceId, int? PartyId, string? Name);
