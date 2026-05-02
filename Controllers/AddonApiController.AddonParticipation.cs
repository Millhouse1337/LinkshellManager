using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class AddonApiController
{
    // Break-room support â€” mirrors the activity endpoints (TakeBreak / ForceBreak /
    // ReturnFromBreak / ForceResume / VerifyReturn / DenyReturn) with the same
    // permission rules: a participant can act on themselves; only token issuers
    // whose linkshell membership has CanModerateLiveEvent can act on someone else
    // or verify/deny pending self-returns. Self is identified by matching the
    // participant's AppUserId to the token's IssuedToAppUserId.

    [HttpGet("events/{eventId:int}/participants")]
    [AddonApiAuth]
    public async Task<IActionResult> ListParticipantsAsync(int eventId, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null) return NotFound(new { error = "Event not found." });
        if (eventEntity.LinkshellId != token.LinkshellId) return Forbid();

        var participations = await _dbContext.AppUserEvents
            .Where(p => p.EventId == eventId)
            .OrderBy(p => p.CharacterName)
            .ToListAsync(cancellationToken);

        // One DB hit for all pending self-return ledger entries on this event.
        var pendingByParticipantId = await _dbContext.AppUserEventStatusLedgers
            .Where(l => l.EventId == eventId
                && l.ActionType == "BreakReturn"
                && l.RequiresVerification
                && l.VerifiedAt == null
                && l.DeniedAt == null)
            .ToListAsync(cancellationToken);
        var pendingMap = pendingByParticipantId
            .GroupBy(l => l.AppUserEventId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.OccurredAt).First());

        var canModerate = await TokenIssuerCanModerateAsync(token, eventEntity.LinkshellId, cancellationToken);

        // Pull commencement once for the addon's per-row live timer math â€”
        // accumulatedHours covers prior breaks, and the addon adds the live
        // segment (now - resumeTime/startTime/commencement) when not on break.
        var rows = participations.Select(p => new
        {
            id = p.Id,
            characterName = p.CharacterName,
            jobName = p.JobName,
            subJobName = p.SubJobName,
            isOnBreak = p.IsOnBreak == true,
            startTime = p.StartTime,
            pauseTime = p.PauseTime,
            resumeTime = p.ResumeTime,
            accumulatedHours = p.Duration,
            isSelf = !string.IsNullOrEmpty(token.IssuedToAppUserId)
                && string.Equals(p.AppUserId, token.IssuedToAppUserId, StringComparison.OrdinalIgnoreCase),
            pendingReturnLedgerId = pendingMap.TryGetValue(p.Id, out var pending) ? pending.Id : (int?)null,
            pendingReturnAt = pendingMap.TryGetValue(p.Id, out var pendingAt) ? pendingAt.OccurredAt : (DateTime?)null
        }).ToList();

        return Ok(new { canModerateLiveEvent = canModerate, participants = rows });
    }

    [HttpPost("events/{eventId:int}/break")]
    [AddonApiAuth]
    public async Task<IActionResult> BreakAsync(
        int eventId,
        [FromBody] AddonBreakRequest request,
        CancellationToken cancellationToken)
    {
        var ctx = await ResolveBreakContextAsync(eventId, request.ParticipantId, cancellationToken);
        if (ctx.Error is not null) return ctx.Error;

        var participation = ctx.Participation!;
        var eventEntity = ctx.EventEntity!;
        if (participation.IsOnBreak == true)
        {
            return BadRequest(new { error = "That participant is already on break." });
        }

        var nowUtc = DateTime.UtcNow;
        participation.Duration = EventController.CalculateAccumulatedDurationHours(
            participation, nowUtc, eventEntity.CommencementStartTime);
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

    [HttpPost("events/{eventId:int}/break/return")]
    [AddonApiAuth]
    public async Task<IActionResult> ReturnFromBreakAsync(
        int eventId,
        [FromBody] AddonBreakRequest request,
        CancellationToken cancellationToken)
    {
        var ctx = await ResolveBreakContextAsync(eventId, request.ParticipantId, cancellationToken);
        if (ctx.Error is not null) return ctx.Error;

        var participation = ctx.Participation!;
        if (participation.IsOnBreak != true)
        {
            return BadRequest(new { error = "That participant is not on break." });
        }

        var nowUtc = DateTime.UtcNow;
        participation.IsOnBreak = false;
        participation.PauseTime = null;
        participation.ResumeTime = nowUtc;

        // Officer-driven returns auto-verify any pending self-return ledger entries
        // for this participant, mirroring ForceResumeAsync. Self-driven returns
        // create a new RequiresVerification entry so an officer can confirm.
        if (ctx.IsModeratorAction)
        {
            var pendingReturns = await _dbContext.AppUserEventStatusLedgers
                .Where(entry => entry.AppUserEventId == participation.Id
                    && entry.ActionType == "BreakReturn"
                    && entry.RequiresVerification
                    && entry.VerifiedAt == null
                    && entry.DeniedAt == null)
                .ToListAsync(cancellationToken);
            var verifierName = await ResolveTokenIssuerNameAsync(ctx.Token!, cancellationToken);
            foreach (var pending in pendingReturns)
            {
                pending.VerifiedAt = nowUtc;
                pending.VerifiedBy = verifierName;
                pending.RequiresVerification = false;
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
                VerifiedBy = verifierName
            });
        }
        else
        {
            _dbContext.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
            {
                AppUserEventId = participation.Id,
                EventId = eventId,
                AppUserId = participation.AppUserId,
                ActionType = "BreakReturn",
                OccurredAt = nowUtc,
                RequiresVerification = true
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/verify-return")]
    [AddonApiAuth]
    public async Task<IActionResult> VerifyReturnAsync(
        int eventId,
        [FromBody] AddonVerifyReturnRequest request,
        CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null) return NotFound(new { error = "Event not found." });
        if (eventEntity.LinkshellId != token.LinkshellId) return Forbid();
        if (!await TokenIssuerCanModerateAsync(token, eventEntity.LinkshellId, cancellationToken)) return Forbid();

        var entry = await _dbContext.AppUserEventStatusLedgers
            .FirstOrDefaultAsync(item => item.Id == request.LedgerEntryId
                && item.EventId == eventId
                && item.ActionType == "BreakReturn", cancellationToken);
        if (entry is null) return NotFound(new { error = "Ledger entry not found." });
        if (!entry.RequiresVerification || entry.VerifiedAt.HasValue)
        {
            return BadRequest(new { error = "That break return has already been verified." });
        }

        entry.VerifiedAt = DateTime.UtcNow;
        entry.VerifiedBy = await ResolveTokenIssuerNameAsync(token, cancellationToken);
        entry.RequiresVerification = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/deny-return")]
    [AddonApiAuth]
    public async Task<IActionResult> DenyReturnAsync(
        int eventId,
        [FromBody] AddonVerifyReturnRequest request,
        CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null) return NotFound(new { error = "Event not found." });
        if (eventEntity.LinkshellId != token.LinkshellId) return Forbid();
        if (!await TokenIssuerCanModerateAsync(token, eventEntity.LinkshellId, cancellationToken)) return Forbid();

        var entry = await _dbContext.AppUserEventStatusLedgers
            .FirstOrDefaultAsync(item => item.Id == request.LedgerEntryId
                && item.EventId == eventId
                && item.ActionType == "BreakReturn", cancellationToken);
        if (entry is null) return NotFound(new { error = "Ledger entry not found." });
        if (!entry.RequiresVerification || entry.VerifiedAt.HasValue || entry.DeniedAt.HasValue)
        {
            return BadRequest(new { error = "That break return is no longer pending." });
        }

        entry.DeniedAt = DateTime.UtcNow;
        entry.DeniedBy = await ResolveTokenIssuerNameAsync(token, cancellationToken);
        entry.RequiresVerification = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("events/{eventId:int}/attendance")]
    [AddonApiAuth]
    public async Task<IActionResult> PostAttendanceAsync(
        int eventId,
        [FromBody] AddonAttendanceRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Entries is null || request.Entries.Count == 0)
        {
            return BadRequest(new { error = "At least one attendance entry is required." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = request.RecordedAtUtc ?? DateTime.UtcNow;

        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "Event not found." });
        }

        if (eventEntity.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }

        // Auto-commence the event if it hasn't started yet (so attendance has a meaningful base time).
        if (eventEntity.CommencementStartTime is null)
        {
            eventEntity.CommencementStartTime = nowUtc;
        }

        // For HNM events the addon may pin this batch to a specific spawn window
        // (1..GetWindowCount). Find or create the EventAttendanceWindow row up front
        // so per-user inserts below can attach to it.
        EventAttendanceWindow? attendanceWindow = null;
        if (request.WindowSequence is int windowSequence)
        {
            var maxWindows = HnmConfig.GetWindowCount(eventEntity.EventName);
            if (windowSequence < 1 || windowSequence > maxWindows)
            {
                return BadRequest(new { error = $"Window sequence {windowSequence} is out of range for this event (max {maxWindows})." });
            }

            attendanceWindow = await _dbContext.EventAttendanceWindows
                .FirstOrDefaultAsync(
                    w => w.EventId == eventId && w.SequenceNumber == windowSequence,
                    cancellationToken);

            if (attendanceWindow is null)
            {
                attendanceWindow = new EventAttendanceWindow
                {
                    EventId = eventId,
                    SequenceNumber = windowSequence,
                    Label = HnmConfig.GetDefaultWindowLabel(eventEntity.EventName, windowSequence, eventEntity.WindowCountOverride),
                    PostedAt = nowUtc,
                    PostedBySource = AddonSource
                };
                _dbContext.EventAttendanceWindows.Add(attendanceWindow);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        var verifiedBy = (token.Label ?? "att-addon") + " (att)";

        var matched = 0;
        var alreadyVerified = 0;
        var unmatched = new List<string>();
        var ledgerIds = new List<int>();

        // Pre-load all linkshell memberships in one query so we can match without a roundtrip per entry.
        var memberships = await _dbContext.AppUserLinkshells
            .Where(m => m.LinkshellId == token.LinkshellId && m.AppUserId != null)
            .ToListAsync(cancellationToken);

        var membershipByName = memberships
            .Where(m => !string.IsNullOrWhiteSpace(m.CharacterName))
            .GroupBy(m => m.CharacterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in request.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.CharacterName)) continue;

            var name = entry.CharacterName.Trim();
            if (!membershipByName.TryGetValue(name, out var membership))
            {
                unmatched.Add(name);
                continue;
            }

            var existing = await _dbContext.AppUserEvents
                .FirstOrDefaultAsync(
                    ue => ue.EventId == eventId && ue.AppUserId == membership.AppUserId,
                    cancellationToken);

            // For windowed events we count "matched" per posted window so re-posting
            // the same name to a different window still bumps the count.
            // For non-windowed events we keep the legacy "first verify wins" behavior.
            var firstTimeVerified = existing is null || existing.IsVerified != true;

            AppUserEvent participation;
            if (existing is null)
            {
                participation = new AppUserEvent
                {
                    AppUserId = membership.AppUserId,
                    EventId = eventId,
                    CharacterName = membership.CharacterName,
                    JobName = string.IsNullOrWhiteSpace(entry.MainJob) ? null : entry.MainJob.Trim(),
                    SubJobName = string.IsNullOrWhiteSpace(entry.SubJob) ? null : entry.SubJob.Trim(),
                    JobType = null,
                    StartTime = nowUtc,
                    IsQuickJoin = true,
                    IsVerified = true
                };
                _dbContext.AppUserEvents.Add(participation);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            else
            {
                participation = existing;
                if (participation.IsVerified == true && attendanceWindow is null)
                {
                    // Legacy single-window event: re-posting an already-verified user is a no-op.
                    alreadyVerified++;
                    continue;
                }

                if (participation.IsVerified != true)
                {
                    participation.IsVerified = true;
                    if (participation.StartTime is null) participation.StartTime = nowUtc;
                }
                if (string.IsNullOrWhiteSpace(participation.JobName) && !string.IsNullOrWhiteSpace(entry.MainJob))
                {
                    participation.JobName = entry.MainJob.Trim();
                }
                if (string.IsNullOrWhiteSpace(participation.SubJobName) && !string.IsNullOrWhiteSpace(entry.SubJob))
                {
                    participation.SubJobName = entry.SubJob.Trim();
                }
            }

            // Per-window join row: silently skip if the user was already credited for this window.
            if (attendanceWindow is not null)
            {
                var alreadyAttendedThisWindow = await _dbContext.AppUserEventWindows
                    .AnyAsync(
                        w => w.AppUserEventId == participation.Id
                          && w.EventAttendanceWindowId == attendanceWindow.Id,
                        cancellationToken);

                if (alreadyAttendedThisWindow)
                {
                    alreadyVerified++;
                    continue;
                }

                _dbContext.AppUserEventWindows.Add(new AppUserEventWindow
                {
                    AppUserEventId = participation.Id,
                    EventAttendanceWindowId = attendanceWindow.Id,
                    VerifiedAt = nowUtc,
                    VerifiedBy = verifiedBy,
                    Zone = string.IsNullOrWhiteSpace(entry.Zone) ? null : entry.Zone.Trim()
                });
            }

            matched++;

            var ledger = new AppUserEventStatusLedger
            {
                AppUserEventId = participation.Id,
                EventId = eventId,
                AppUserId = membership.AppUserId,
                ActionType = "Verify",
                OccurredAt = nowUtc,
                RequiresVerification = false,
                VerifiedAt = nowUtc,
                VerifiedBy = verifiedBy,
                Source = AddonSource,
                EventAttendanceWindowId = attendanceWindow?.Id
            };
            _dbContext.AppUserEventStatusLedgers.Add(ledger);
            await _dbContext.SaveChangesAsync(cancellationToken);
            ledgerIds.Add(ledger.Id);
        }

        return Ok(new
        {
            matched,
            alreadyVerified,
            unmatched,
            ledgerEntryIds = ledgerIds,
            windowSequence = attendanceWindow?.SequenceNumber,
            windowId = attendanceWindow?.Id,
            windowLabel = attendanceWindow?.Label
        });
    }

    // Token-authenticated remove: lets the addon undo an accidental window post for
    // a single character without nuking the whole event. The path uses the same shape
    // as the other addon endpoints (event id + window sequence) and matches the
    // attendee by character name (case-insensitive on the trimmed value).
    [HttpDelete("events/{eventId:int}/windows/{sequence:int}/attendees/{characterName}")]
    [AddonApiAuth]
    public async Task<IActionResult> RemoveWindowAttendeeAddonAsync(
        int eventId, int sequence, string characterName, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var attendanceWindow = await _dbContext.EventAttendanceWindows
            .Include(w => w.Event)
            .Include(w => w.Attendees)
                .ThenInclude(a => a.AppUserEvent)
            .FirstOrDefaultAsync(
                w => w.EventId == eventId && w.SequenceNumber == sequence,
                cancellationToken);

        if (attendanceWindow is null) return NotFound(new { error = "Window not found." });
        if (attendanceWindow.Event is null || attendanceWindow.Event.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }

        var trimmed = (characterName ?? string.Empty).Trim();
        var attendee = attendanceWindow.Attendees.FirstOrDefault(a =>
            a.AppUserEvent != null
            && string.Equals(a.AppUserEvent.CharacterName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (attendee is null) return NotFound(new { error = "Attendee not found in this window." });

        await RemoveWindowAttendeeRowAsync(attendee, cancellationToken);
        return Ok(new { removedId = attendee.Id });
    }

    // Returns the linkshell roster + its loot structure so the addon can
    // populate the Winner combo on the Loot Pool panel and label the DKP
    // field correctly (raw DKP for "Dkp", percentage for "Hybrid",
    // disabled for "LootCouncil"). Token-scoped: the user only sees the
    // roster of the linkshell their pairing is bound to.
    [HttpGet("roster")]
    [AddonApiAuth]
    public async Task<IActionResult> GetRosterAsync(CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var linkshell = await _dbContext.Linkshells
            .FirstOrDefaultAsync(ls => ls.Id == token.LinkshellId, cancellationToken);

        var characterNames = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == token.LinkshellId
                        && !string.IsNullOrWhiteSpace(link.CharacterName))
            .Select(link => link.CharacterName!)
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            characterNames,
            lootStructure = ActivityDataController.NormalizeLootStructure(linkshell?.LootStructure ?? "Dkp")
        });
    }

    // Posts a single TodLootDetail row attached to an existing Tod. Mirrors
    // the Discord Activity create-tod-loot flow but accepts one item per
    // call (matches the per-row "Post Loot" UX in the addon launcher) and
    // is gated by addon-token auth on the parent Tod's linkshell.
    [HttpPost("tod/{todId:int}/loot")]
    [AddonApiAuth]
    public async Task<IActionResult> PostTodLootAsync(
        int todId,
        [FromBody] AddonPostLootRequest request,
        CancellationToken cancellationToken)
    {
        var itemName   = request.ItemName?.Trim();
        var itemWinner = request.ItemWinner?.Trim();
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return BadRequest(new { error = "Item name is required." });
        }
        if (string.IsNullOrWhiteSpace(itemWinner))
        {
            return BadRequest(new { error = "Item winner is required." });
        }
        if (!request.WinningDkpSpent.HasValue || request.WinningDkpSpent.Value <= 0)
        {
            return BadRequest(new { error = "WinningDkpSpent must be a positive number." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = DateTime.UtcNow;

        var tod = await _dbContext.Tods
            .Include(t => t.Linkshell)
            .Include(t => t.TodLootDetails)
            .FirstOrDefaultAsync(t => t.Id == todId, cancellationToken);
        if (tod is null || tod.LinkshellId != token.LinkshellId)
        {
            return NotFound(new { error = "Tod not found." });
        }

        var lootStructure = ActivityDataController.NormalizeLootStructure(tod.Linkshell?.LootStructure ?? "Dkp");
        if (lootStructure == "LootCouncil")
        {
            return BadRequest(new { error = "Linkshell uses LootCouncil â€” DKP loot posts are disabled." });
        }
        if (lootStructure == "Hybrid" && request.WinningDkpSpent.Value > 100)
        {
            return BadRequest(new { error = "Deduction % cannot exceed 100." });
        }

        // Validate winner is in the linkshell's roster (case-insensitive match
        // on CharacterName) â€” same guard CreateTodAsync uses.
        var rosterMatch = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == token.LinkshellId
                        && link.CharacterName != null
                        && link.CharacterName.ToLower() == itemWinner.ToLower())
            .Select(link => link.CharacterName!)
            .FirstOrDefaultAsync(cancellationToken);
        if (rosterMatch is null)
        {
            return BadRequest(new { error = "Winner must be a current linkshell member." });
        }

        var detail = new TodLootDetail
        {
            TodId = tod.Id,
            ItemName = itemName,
            ItemWinner = rosterMatch,
            WinningDkpSpent = request.WinningDkpSpent
        };

        _dbContext.TodLootDetails.Add(detail);
        await ActivityDataController.AdjustTodLootDkpAsync(
            _dbContext, tod, new[] { detail }, nowUtc, isRefund: false, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            lootDetailId      = detail.Id,
            itemName          = detail.ItemName,
            itemWinner        = detail.ItemWinner,
            winningDkpSpent   = detail.WinningDkpSpent,
            actualDeductedDkp = detail.ActualDeductedDkp
        });
    }

    // Addon-side ToD posting. Mirrors the Discord Activity create-tod path in
    // ActivityDataController but with addon-token auth (linkshell-scoped) and
    // a slimmer payload â€” the addon only knows the monster name and the
    // wall-clock kill time, so cooldown/interval defaults are filled in
    // server-side using the same helpers the Discord Activity uses.
    [HttpPost("tod")]
    [AddonApiAuth]
    public async Task<IActionResult> PostTodAsync(
        [FromBody] AddonPostTodRequest request,
        CancellationToken cancellationToken)
    {
        var monsterName = request.MonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            return BadRequest(new { error = "Monster name is required." });
        }

        if (!request.DefeatedAtUtc.HasValue)
        {
            return BadRequest(new { error = "DefeatedAtUtc is required." });
        }

        var defeatedAtUtc = DateTime.SpecifyKind(request.DefeatedAtUtc.Value, DateTimeKind.Utc);
        if (defeatedAtUtc > DateTime.UtcNow.AddMinutes(5))
        {
            return BadRequest(new { error = "DefeatedAtUtc cannot be in the future." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = DateTime.UtcNow;

        var cooldown = ActivityDataController.GetDefaultTodCooldown(monsterName);
        var interval = ActivityDataController.GetDefaultTodInterval(monsterName);
        var repopTimeUtc = defeatedAtUtc.AddHours(ActivityDataController.ResolveTodCooldownHours(cooldown));

        var tod = new Tod
        {
            LinkshellId = token.LinkshellId,
            MonsterName = monsterName,
            DayNumber = null,
            Claim = false,
            Time = defeatedAtUtc,
            Cooldown = cooldown,
            RepopTime = repopTimeUtc,
            Interval = interval,
            TimeStamp = nowUtc,
            TotalTods = 1,
            TotalClaims = 0,
            ImagePath = null
        };

        _dbContext.Tods.Add(tod);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            todId = tod.Id,
            monsterName = tod.MonsterName,
            defeatedAtUtc = tod.Time,
            repopTimeUtc = tod.RepopTime,
            cooldown = tod.Cooldown,
            interval = tod.Interval
        });
    }
}
