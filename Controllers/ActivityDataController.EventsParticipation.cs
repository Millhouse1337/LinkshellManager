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

        var displayName = appUser.CharacterName ?? appUser.UserName ?? "Unknown";

        if (request.JobId <= 0)
        {
            var eventEntity = await _dbContext.Events
                .Include(item => item.Jobs)
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);

            if (eventEntity is null)
            {
                return NotFound(new { error = "The selected event was not found." });
            }

            if (eventEntity.Jobs.Count > 0)
            {
                return BadRequest(new { error = "A job selection is required." });
            }

            var existingNoJobSignup = await _dbContext.AppUserEvents
                .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

            if (existingNoJobSignup is not null)
            {
                _dbContext.AppUserEvents.Remove(existingNoJobSignup);
            }

            // For events with no pre-defined party setup, accept the user's
            // ad-hoc Main/Sub/Role from the body. Strings are trimmed and
            // null-coalesced so blank picks land as null instead of "".
            static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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

            await _dbContext.SaveChangesAsync(cancellationToken);
            return Ok(new { success = true });
        }

        var job = await _dbContext.Jobs
            .Include(item => item.Event)
            .FirstOrDefaultAsync(item => item.Id == request.JobId && item.EventId == eventId, cancellationToken);

        if (job?.Event is null)
        {
            return NotFound(new { error = "The selected event job was not found." });
        }

        var existingSignup = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

        if (existingSignup is not null)
        {
            var previousJob = await _dbContext.Jobs
                .FirstOrDefaultAsync(item =>
                    item.EventId == eventId &&
                    item.JobName == existingSignup.JobName &&
                    item.SubJobName == existingSignup.SubJobName,
                    cancellationToken);

            if (previousJob is not null)
            {
                previousJob.Enlisted.RemoveAll(name => name == existingSignup.CharacterName || name == displayName);
                previousJob.SignedUp = previousJob.Enlisted.Count;
            }

            _dbContext.AppUserEvents.Remove(existingSignup);
        }

        job.Enlisted ??= new List<string>();
        if (!job.Enlisted.Contains(displayName))
        {
            job.Enlisted.Add(displayName);
        }

        job.SignedUp = job.Enlisted.Count;

        _dbContext.AppUserEvents.Add(new AppUserEvent
        {
            AppUserId = appUser.Id,
            EventId = eventId,
            CharacterName = displayName,
            JobName = job.JobName,
            SubJobName = job.SubJobName,
            JobType = job.JobType,
            EventDkp = 0,
            StartTime = job.Event.CommencementStartTime
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
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

        var hasLinkshellMembership = await _dbContext.AppUserLinkshells
            .AnyAsync(link => link.AppUserId == appUser.Id && link.LinkshellId == eventEntity.LinkshellId, cancellationToken);

        if (!hasLinkshellMembership)
        {
            return Forbid();
        }

        var existingSignup = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

        if (existingSignup is not null)
        {
            return BadRequest(new { error = "You are already attached to this live event." });
        }

        _dbContext.AppUserEvents.Add(new AppUserEvent
        {
            AppUserId = appUser.Id,
            EventId = eventId,
            CharacterName = appUser.CharacterName,
            JobName = request.JobName.Trim(),
            SubJobName = request.SubJobName.Trim(),
            JobType = request.JobType.Trim(),
            StartTime = DateTime.UtcNow,
            EventDkp = 0,
            IsQuickJoin = true
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
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

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

        if (participation is null)
        {
            return NotFound(new { error = "No signup was found for the current app user." });
        }

        var job = await _dbContext.Jobs
            .FirstOrDefaultAsync(item =>
                item.EventId == eventId &&
                item.JobName == participation.JobName &&
                item.SubJobName == participation.SubJobName,
                cancellationToken);

        if (job is not null)
        {
            var displayName = appUser.CharacterName ?? appUser.UserName ?? "Unknown";
            job.Enlisted.RemoveAll(name => name == participation.CharacterName || name == displayName);
            job.SignedUp = job.Enlisted.Count;
        }

        _dbContext.AppUserEvents.Remove(participation);
        await _dbContext.SaveChangesAsync(cancellationToken);

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
