using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

        var setup = ev.PartySetup;
        var alliances = setup.Alliances
            .OrderBy(a => a.SortOrder)
            .Select(a => new ActivityPartySetupAllianceDto(
                string.IsNullOrWhiteSpace(a.Name) ? $"Alliance {a.SortOrder + 1}" : a.Name,
                a.Parties
                    .OrderBy(p => p.SortOrder)
                    .Select(p => new ActivityPartySetupPartyDto(
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
                                    su?.SubJob);
                            })
                            .ToList()))
                    .ToList()))
            .ToList();

        return Ok(new ActivityPartySetupDetailDto(
            setup.Id, setup.LinkshellId, setup.Name, setup.AssignedMonsterName, setup.Notes, canManage, alliances));
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

        var characterName = string.IsNullOrWhiteSpace(membership.CharacterName)
            ? (appUser.CharacterName ?? appUser.UserName ?? "Member")
            : membership.CharacterName;
        var result = await EventPartySignupService.ClaimSlotAsync(
            _dbContext, eventId, slot, appUser.Id, characterName, request.Role, request.MainJob, request.SubJob, cancellationToken);
        if (!result.Success) return BadRequest(new { error = result.Error });
        await _dbContext.SaveChangesAsync(cancellationToken);
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
            // can clear anyone's.
            var isHolder = signup.AppUserId == appUser.Id;
            if (!isHolder && !await CanAsync(membership, r => r.CanManageParties, cancellationToken))
            {
                return Forbid();
            }
            _dbContext.EventPartySlotSignups.Remove(signup);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Ok(new { success = true });
    }
}
