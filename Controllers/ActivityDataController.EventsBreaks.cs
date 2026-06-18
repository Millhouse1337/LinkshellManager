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
    [HttpPost("events/{eventId:int}/break")]
    public async Task<IActionResult> TakeBreakAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update break status."
            });
        }

        var eventEntity = await _dbContext.Events.AsNoTracking().FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Break status is only available after the event has started." });
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

        if (participation is null)
        {
            return BadRequest(new { error = "Join the live event before taking a break." });
        }

        if (participation.IsOnBreak == true)
        {
            return BadRequest(new { error = "You are already marked as on break." });
        }

        var nowUtc = DateTime.UtcNow;
        participation.Duration = CalculateAccumulatedDurationHours(participation, nowUtc, eventEntity.CommencementStartTime);
        participation.IsOnBreak = true;
        participation.PauseTime = nowUtc;
        participation.ResumeTime = null;
        _dbContext.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
        {
            AppUserEventId = participation.Id,
            EventId = eventId,
            AppUserId = appUser.Id,
            ActionType = "BreakStart",
            OccurredAt = nowUtc,
            RequiresVerification = false
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/break/return")]
    public async Task<IActionResult> ReturnFromBreakAsync(int eventId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update break status."
            });
        }

        var eventEntity = await _dbContext.Events.AsNoTracking().FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Break status is only available after the event has started." });
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);

        if (participation is null)
        {
            return BadRequest(new { error = "Join the live event before returning from break." });
        }

        if (participation.IsOnBreak != true)
        {
            return BadRequest(new { error = "You are not currently marked as on break." });
        }

        participation.IsOnBreak = false;
        participation.PauseTime = null;
        participation.ResumeTime = DateTime.UtcNow;
        // They came back after all — drop the "not returning" mark a Withdraw set.
        participation.WithdrewFromEvent = false;
        _dbContext.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
        {
            AppUserEventId = participation.Id,
            EventId = eventId,
            AppUserId = appUser.Id,
            ActionType = "BreakReturn",
            OccurredAt = participation.ResumeTime.Value,
            RequiresVerification = true
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/break/force")]
    public async Task<IActionResult> ForceBreakAsync(
        int eventId,
        [FromBody] ActivityForceBreakRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to send a member to the break room."
            });
        }

        var eventEntity = await _dbContext.Events.AsNoTracking().FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Break status is only available after the event has started." });
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.Id == request.ParticipantId && item.EventId == eventId, cancellationToken);

        if (participation is null)
        {
            return NotFound(new { error = "The selected participant was not found." });
        }

        if (participation.IsOnBreak == true)
        {
            return BadRequest(new { error = "That member is already marked as on break." });
        }

        var nowUtc = DateTime.UtcNow;
        participation.Duration = CalculateAccumulatedDurationHours(participation, nowUtc, eventEntity.CommencementStartTime);
        participation.IsOnBreak = true;
        participation.PauseTime = nowUtc;
        participation.ResumeTime = null;
        _dbContext.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
        {
            AppUserEventId = participation.Id,
            EventId = eventId,
            AppUserId = participation.AppUserId,
            ActionType = "BreakStart",
            OccurredAt = nowUtc,
            RequiresVerification = false
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/break/resume/force")]
    public async Task<IActionResult> ForceResumeAsync(
        int eventId,
        [FromBody] ActivityForceResumeRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to resume a member."
            });
        }

        var eventEntity = await _dbContext.Events.AsNoTracking().FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Break status is only available after the event has started." });
        }

        var participation = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(item => item.Id == request.ParticipantId && item.EventId == eventId, cancellationToken);

        if (participation is null)
        {
            return NotFound(new { error = "The selected participant was not found." });
        }

        if (participation.IsOnBreak != true)
        {
            return BadRequest(new { error = "That member is not currently on break." });
        }

        var nowUtc = DateTime.UtcNow;
        participation.IsOnBreak = false;
        participation.PauseTime = null;
        participation.ResumeTime = nowUtc;
        // Officer resumed them — clear any "not returning" mark from a Withdraw.
        participation.WithdrewFromEvent = false;

        var pendingReturns = await _dbContext.AppUserEventStatusLedgers
            .Where(entry =>
                entry.AppUserEventId == participation.Id &&
                entry.ActionType == "BreakReturn" &&
                entry.RequiresVerification &&
                entry.VerifiedAt == null &&
                entry.DeniedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var pending in pendingReturns)
        {
            pending.VerifiedAt = nowUtc;
            pending.VerifiedBy = appUser.CharacterName ?? appUser.UserName;
        }

        _dbContext.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
        {
            AppUserEventId = participation.Id,
            EventId = eventId,
            AppUserId = participation.AppUserId,
            ActionType = "BreakReturn",
            OccurredAt = nowUtc,
            RequiresVerification = false,
            VerifiedAt = nowUtc,
            VerifiedBy = appUser.CharacterName ?? appUser.UserName
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/verify-return")]
    public async Task<IActionResult> VerifyReturnAsync(
        int eventId,
        [FromBody] ActivityVerifyReturnRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to verify a break return."
            });
        }

        var eventEntity = await _dbContext.Events.AsNoTracking().FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        var ledgerEntry = await _dbContext.AppUserEventStatusLedgers
            .FirstOrDefaultAsync(
                item => item.Id == request.LedgerEntryId &&
                        item.EventId == eventId &&
                        item.ActionType == "BreakReturn",
                cancellationToken);

        if (ledgerEntry is null)
        {
            return NotFound(new { error = "The selected ledger entry was not found." });
        }

        if (!ledgerEntry.RequiresVerification || ledgerEntry.VerifiedAt.HasValue)
        {
            return BadRequest(new { error = "That break return has already been verified." });
        }

        ledgerEntry.VerifiedAt = DateTime.UtcNow;
        ledgerEntry.VerifiedBy = appUser.CharacterName ?? appUser.UserName;
        ledgerEntry.RequiresVerification = false;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/deny-return")]
    public async Task<IActionResult> DenyReturnAsync(
        int eventId,
        [FromBody] ActivityVerifyReturnRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to deny a break return."
            });
        }

        var eventEntity = await _dbContext.Events.AsNoTracking().FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanModerateLiveEvent, cancellationToken))
        {
            return Forbid();
        }

        var ledgerEntry = await _dbContext.AppUserEventStatusLedgers
            .FirstOrDefaultAsync(
                item => item.Id == request.LedgerEntryId &&
                        item.EventId == eventId &&
                        item.ActionType == "BreakReturn",
                cancellationToken);

        if (ledgerEntry is null)
        {
            return NotFound(new { error = "The selected ledger entry was not found." });
        }

        if (!ledgerEntry.RequiresVerification || ledgerEntry.VerifiedAt.HasValue || ledgerEntry.DeniedAt.HasValue)
        {
            return BadRequest(new { error = "That break return has already been resolved." });
        }

        ledgerEntry.DeniedAt = DateTime.UtcNow;
        ledgerEntry.DeniedBy = appUser.CharacterName ?? appUser.UserName;
        ledgerEntry.RequiresVerification = false;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Parks a live participant in the Break Room: banks their accrued time and pauses
    // the timer (IsOnBreak) WITHOUT removing them, so DKP / attendance / event history
    // are all preserved and a later return resumes exactly where they left off. Shared
    // by the self-service break flow and "Withdraw From Event" (which now parks a live
    // member here instead of deleting their participation). The caller saves; skip the
    // call when the participant is already on break.
    private void PutParticipationOnBreak(
        AppUserEvent participation, int eventId, DateTime nowUtc, DateTime? commencementStart)
    {
        participation.Duration = CalculateAccumulatedDurationHours(participation, nowUtc, commencementStart);
        participation.IsOnBreak = true;
        participation.PauseTime = nowUtc;
        participation.ResumeTime = null;
        _dbContext.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
        {
            AppUserEventId = participation.Id,
            EventId = eventId,
            AppUserId = participation.AppUserId,
            ActionType = "BreakStart",
            OccurredAt = nowUtc,
            RequiresVerification = false
        });
    }
}
