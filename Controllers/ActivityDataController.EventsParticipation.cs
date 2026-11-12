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

        var eventEntity = await _dbContext.Events
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        // Only members of the event's linkshell may sign up. Without this anyone could put
        // their name on another linkshell's roster and Discord board by trying event ids.
        // GetMembershipAsync also enforces the per-linkshell Discord guild lock.
        var membership = await GetMembershipAsync(appUser.Id, eventEntity.LinkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var displayName = SignupCharacters.Resolve(appUser, membership, request.CharacterName);

        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var appUserId = appUser.Id;

        // Re-run if a second sign-up for this member (a double click) inserted their row first: the
        // one-participation-per-event index turns ours away, and the re-run finds that row and
        // switches its job in place.
        await UniqueRaceRetry.RunAsync(_dbContext, async () =>
        {
            var existing = await _dbContext.AppUserEvents
                .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUserId, cancellationToken);

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
                _dbContext.AppUserEvents.Add(NewSignupRow(
                    appUserId,
                    eventEntity,
                    displayName,
                    Clean(request.JobName),
                    Clean(request.SubJobName),
                    Clean(request.JobType),
                    DateTime.UtcNow));
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }, UniqueRaceRetry.Participations);
        EnqueueEventBoardRefresh(eventId);
        return Ok(new { success = true });
    }

    // The attendance row a new /signup creates. The clock follows SignupClock: empty
    // before the event starts, NOW (a late join) once it is live.
    internal static AppUserEvent NewSignupRow(
        string appUserId,
        Event eventEntity,
        string characterName,
        string? jobName,
        string? subJobName,
        string? jobType,
        DateTime nowUtc)
    {
        var (startTime, isQuickJoin) = SignupClock.ForNewSignup(eventEntity, nowUtc);
        return new AppUserEvent
        {
            AppUserId = appUserId,
            EventId = eventEntity.Id,
            CharacterName = characterName,
            JobName = jobName,
            SubJobName = subJobName,
            JobType = jobType,
            EventDkp = 0,
            StartTime = startTime,
            IsQuickJoin = isQuickJoin
        };
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
        var appUserId = appUser.Id;

        // Same double-click re-run as SignUpAsync: a lost race finds the other request's row and
        // updates it in place.
        await UniqueRaceRetry.RunAsync(_dbContext, async () =>
        {
            var existingSignup = await _dbContext.AppUserEvents
                .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUserId, cancellationToken);

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
                    AppUserId = appUserId,
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
            return true;
        }, UniqueRaceRetry.Participations);
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

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (UniqueRaceRetry.IsDuplicateParticipation(ex))
        {
            // They joined (or another officer added them) between the check above and this save.
            return BadRequest(new { error = "That member is already attached to this live event." });
        }
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

        var eventEntity = await _dbContext.Events.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "The selected event was not found." });
        }

        // LIVE event: "Withdraw From Event" parks the member in the Break Room rather
        // than removing them — their timer pauses and their DKP / attendance / event
        // history are preserved, so a return (Resume from break) picks up exactly where
        // they left off. Their party slot is kept too. Idempotent if already on break.
        //
        // ...but ONLY for a timed event. A windowed (HNM) camp credits per posted window, so
        // there is no timer to pause and parking someone here is a self-service way to strand
        // themselves — see the INVARIANT on PutParticipationOnBreak. Plain-removing them instead
        // is NOT an alternative: AppUserEventWindow cascade-deletes with AppUserEvent
        // (ApplicationDbContext), so it would destroy the window credit they already earned.
        // The Activity hides the button while a windowed camp is live; this is the backstop.
        if (eventEntity.CommencementStartTime.HasValue)
        {
            if (EventBreakPolicy.IsWindowedAttendance(eventEntity))
            {
                return BadRequest(new { error = EventBreakPolicy.WindowedWithdrawRejectionMessage });
            }

            var live = await _dbContext.AppUserEvents
                .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == appUser.Id, cancellationToken);
            if (live is null)
            {
                return NotFound(new { error = "No participation was found for the current app user." });
            }
            if (live.IsOnBreak != true)
            {
                PutParticipationOnBreak(live, eventId, DateTime.UtcNow, eventEntity.CommencementStartTime);
            }
            // Mark intent-to-leave so the Break Room shows a "not returning" label. They're
            // kept in the Break Room (not removed), so if they change their mind a Resume
            // picks up exactly where they left off.
            live.WithdrewFromEvent = true;
            await _dbContext.SaveChangesAsync(cancellationToken);
            EnqueueEventBoardRefresh(eventId);
            return Ok(new { success = true, onBreak = true });
        }

        // PRE-START: there's no accrued time/DKP yet, so withdrawing just cancels the
        // signup — drop the member's party slot (if any) AND their attendance row.
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

        // The winner has to be a member, as on Loot History's Add loot (ManualLootService): a name
        // nobody has was saved with a price and then silently never charged at close. A blank
        // winner is still fine for an item that costs nothing (Loot Council, a free drop).
        var winnerName = request.ItemWinner?.Trim();
        AppUserLinkshell? winner = null;
        if (!string.IsNullOrEmpty(winnerName))
        {
            winner = await LootDkpGuard.ResolveWinnerAsync(
                _dbContext, eventEntity.LinkshellId, winnerName, cancellationToken);
            if (winner?.AppUserId is null)
            {
                return BadRequest(new { error = "Winner must be a current linkshell member." });
            }
        }
        else if ((request.WinningDkpSpent ?? 0) > 0)
        {
            return BadRequest(new { error = "Pick a winner to charge DKP for this item." });
        }

        // Block awarding loot the winner can't afford (DKP is deducted at close,
        // so this is the only point we can stop the balance going negative).
        var insufficient = await LootDkpGuard.CheckEventLootAsync(
            _dbContext, _dkpPools, _dkpPoolBalances, eventId, eventEntity.LinkshellId,
            request.ItemWinner, request.WinningDkpSpent ?? 0, cancellationToken);
        if (insufficient is not null)
        {
            return BadRequest(new { error = insufficient });
        }

        _dbContext.EventLootDetails.Add(new EventLootDetail
        {
            EventId = eventId,
            ItemName = request.ItemName.Trim(),
            ItemWinner = winnerName,
            ItemWinnerAppUserId = winner?.AppUserId,
            WinningDkpSpent = request.WinningDkpSpent,
            RecordedByAppUserId = appUser.Id
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }
}
