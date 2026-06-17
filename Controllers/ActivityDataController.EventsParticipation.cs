using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    // Ad-hoc event signup. Slot-level claiming lives on the linked PartySetup's
    // own signup endpoint (see ActivityDataController.PartySetup.cs).
    [HttpPost("events/{eventId:int}/signup")]
    public async Task<IActionResult> SignUpAsync(int eventId, [FromBody] ActivityEventSignupRequest request, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to sign up for events."
            });
        }

        var displayName = SignupCharacters.Resolve(appUser, null, request.CharacterName);

        var eventEntity = await _dbContext.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        var existing = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

        if (existing is not null)
        {
            // Switching jobs updates in place so accrued time (StartTime / Duration /
            // break state) is preserved instead of restarting the clock.
            existing.CharacterName = displayName;
            existing.JobName = Clean(request.JobName);
            existing.SubJobName = Clean(request.SubJobName);
            existing.JobType = Clean(request.JobType);
        }
        else
        {
            _dbContext.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = appUser.Id,
                EventId = eventId,
                CharacterName = displayName,
                JobName = Clean(request.JobName),
                SubJobName = Clean(request.SubJobName),
                JobType = Clean(request.JobType),
                EventDkp = 0,
                StartTime = eventEntity.CommencementStartTime
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        EnqueueEventBoardRefresh(eventId);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/quick-join")]
    public async Task<IActionResult> QuickJoinAsync(
        int eventId,
        [FromBody] ActivityQuickJoinRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.JobName) ||
            string.IsNullOrWhiteSpace(request.SubJobName) ||
            string.IsNullOrWhiteSpace(request.JobType))
        {
            return BadRequest(new { error = "Job, sub job, and type are required for quick join." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to quick join a live event."
            });
        }

        var eventEntity = await _dbContext.Events.AsNoTracking().FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Quick join is only available after the event has started." });
        }

        // GetMembershipAsync also enforces the per-linkshell Discord guild lock.
        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var character = SignupCharacters.Resolve(appUser, membership, request.CharacterName);
        var existingSignup = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

        if (existingSignup is not null)
        {
            // Already attending → switching jobs updates in place, keeping their
            // accrued time (StartTime / Duration / break state) intact.
            existingSignup.CharacterName = character;
            existingSignup.JobName = request.JobName.Trim();
            existingSignup.SubJobName = request.SubJobName.Trim();
            existingSignup.JobType = request.JobType.Trim();
        }
        else
        {
            _dbContext.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = appUser.Id,
                EventId = eventId,
                CharacterName = character,
                JobName = request.JobName.Trim(),
                SubJobName = request.SubJobName.Trim(),
                JobType = request.JobType.Trim(),
                StartTime = DateTime.UtcNow,
                EventDkp = 0,
                IsQuickJoin = true
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        EnqueueEventBoardRefresh(eventId);
        return Ok(new { success = true });
    }

    [HttpGet("events/{eventId:int}/add-member-candidates")]
    public async Task<IActionResult> GetAddMemberCandidatesAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load event members."
            });
        }

        var eventEntity = await _dbContext.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Members can only be added after the event has started." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        var candidates = await _dbContext.AppUserLinkshells
            .Include(link => link.AppUser)
            .Where(link =>
                link.LinkshellId == eventEntity.LinkshellId &&
                link.AppUserId != null)
            .OrderBy(link => link.CharacterName)
            .ThenBy(link => link.AppUser!.CharacterName)
            .AsNoTracking()
            .Select(link => new ActivityEventAddMemberCandidateDto(
                link.AppUserId!,
                link.CharacterName ?? link.AppUser!.CharacterName ?? link.AppUser!.UserName ?? "Unknown member",
                link.Rank))
            .ToListAsync(cancellationToken);

        return Ok(candidates);
    }

    [HttpPost("events/{eventId:int}/members")]
    public async Task<IActionResult> AddMemberAsync(
        int eventId,
        [FromBody] ActivityAddEventMemberRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AppUserId) ||
            string.IsNullOrWhiteSpace(request.JobName) ||
            string.IsNullOrWhiteSpace(request.SubJobName) ||
            string.IsNullOrWhiteSpace(request.JobType))
        {
            return BadRequest(new { error = "Member, job, sub job, and role are required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to add a member."
            });
        }

        var eventEntity = await _dbContext.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Members can only be added after the event has started." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        var targetMembership = await _dbContext.AppUserLinkshells
            .Include(link => link.AppUser)
            .FirstOrDefaultAsync(link =>
                link.LinkshellId == eventEntity.LinkshellId &&
                link.AppUserId == request.AppUserId,
                cancellationToken);

        if (targetMembership is null)
        {
            return NotFound(new { error = "The selected member is not in this linkshell." });
        }

        var existingParticipant = await _dbContext.AppUserEvents
            .AnyAsync(item => item.EventId == eventId && item.AppUserId == request.AppUserId, cancellationToken);
        if (existingParticipant)
        {
            return BadRequest(new { error = "That member is already attached to this live event." });
        }

        var existingSlotSignup = await _dbContext.EventPartySlotSignups
            .AnyAsync(item => item.EventId == eventId && item.AppUserId == request.AppUserId, cancellationToken);
        if (existingSlotSignup)
        {
            return BadRequest(new { error = "That member is already signed up through the event party board." });
        }

        _dbContext.AppUserEvents.Add(new AppUserEvent
        {
            AppUserId = request.AppUserId,
            EventId = eventId,
            CharacterName = targetMembership.CharacterName ??
                targetMembership.AppUser?.CharacterName ??
                targetMembership.AppUser?.UserName ??
                "Unknown member",
            JobName = request.JobName.Trim(),
            SubJobName = request.SubJobName.Trim(),
            JobType = request.JobType.Trim(),
            StartTime = DateTime.UtcNow,
            EventDkp = 0,
            IsQuickJoin = true,
            IsVerified = null
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        EnqueueEventBoardRefresh(eventId);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/unsign")]
    public async Task<IActionResult> UnsignAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to unsign from events."
            });
        }

        // Full withdrawal: drop the member's party slot (if any) AND their
        // attendance row, so "Withdraw From Event" removes them everywhere — not
        // just the live roster. Mirrors the Discord board's Withdraw button.
        var affectedPartyId = await EventPartySignupService.LeaveAsync(
            _dbContext, eventId, appUser.Id, cancellationToken);

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);
        if (participation is not null)
        {
            _dbContext.AppUserEvents.Remove(participation);
        }

        if (participation is null && affectedPartyId is null)
        {
            return NotFound(new { error = "No signup was found for the current app user." });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // If they vacated a party slot, re-resolve that party's leadership.
        if (affectedPartyId is not null)
        {
            await EventPartySignupService.ResolvePartyLeadershipAsync(
                _dbContext, eventId, affectedPartyId, cancellationToken);
        }

        EnqueueEventBoardRefresh(eventId);

        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/verify")]
    public async Task<IActionResult> VerifyParticipantAsync(
        int eventId,
        [FromBody] ActivityVerifyParticipantRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to verify attendance."
            });
        }

        var eventEntity = await _dbContext.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.Id == request.ParticipantId && item.EventId == eventId, cancellationToken);

        if (participation is null)
        {
            return NotFound(new { error = "The selected participant was not found." });
        }

        if (participation.IsVerified.HasValue)
        {
            return BadRequest(new { error = "Initial attendance has already been verified. Use undo if you need to change it." });
        }

        participation.IsVerified = request.IsVerified;
        participation.Proctor = appUser.CharacterName ?? appUser.UserName;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/verify/reset")]
    public async Task<IActionResult> ResetVerificationAsync(
        int eventId,
        [FromBody] ActivityResetParticipantRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to reset attendance verification."
            });
        }

        var eventEntity = await _dbContext.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.Id == request.ParticipantId && item.EventId == eventId, cancellationToken);

        if (participation is null)
        {
            return NotFound(new { error = "The selected participant was not found." });
        }

        participation.IsVerified = null;
        participation.Proctor = null;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/loot")]
    public async Task<IActionResult> AddLootAsync(
        int eventId,
        [FromBody] ActivityAddLootRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ItemName))
        {
            return BadRequest(new { error = "Item name is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to add loot."
            });
        }

        var eventEntity = await _dbContext.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanAddLoot, cancellationToken))
        {
            return Forbid();
        }

        // Block awarding loot the winner can't afford (DKP is deducted at close,
        // so this is the only point we can stop the balance going negative).
        var insufficient = await LootDkpGuard.CheckEventLootAsync(
            _dbContext, eventId, eventEntity.LinkshellId,
            request.ItemWinner, request.WinningDkpSpent ?? 0, cancellationToken);
        if (insufficient is not null)
        {
            return BadRequest(new { error = insufficient });
        }

        _dbContext.EventLootDetails.Add(new EventLootDetail
        {
            EventId = eventId,
            ItemName = request.ItemName.Trim(),
            ItemWinner = request.ItemWinner?.Trim(),
            WinningDkpSpent = request.WinningDkpSpent
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }
}
