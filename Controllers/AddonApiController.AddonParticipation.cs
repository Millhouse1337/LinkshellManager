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

        // Three-tier check: moderator → immediate post (today's behavior);
        // submit-for-approval → queue a PendingAttendanceWindowSubmission;
        // neither → 403.
        var attendanceRole = await GetTokenIssuerRoleAsync(token, eventEntity.LinkshellId, cancellationToken);
        var canModerate = attendanceRole?.CanModerateLiveEvent == true;
        var canSubmitAttendance = attendanceRole?.CanSubmitAttendanceForApproval == true;
        if (!canModerate && !canSubmitAttendance)
        {
            return Forbid();
        }

        if (!canModerate)
        {
            var approvalSvc = HttpContext.RequestServices.GetRequiredService<SubmissionApprovalService>();
            var members = (request.Entries ?? new List<AddonAttendanceEntry>())
                .Where(e => !string.IsNullOrWhiteSpace(e.CharacterName))
                .Select(e => new AttendanceWindowMemberInput(
                    e.CharacterName!.Trim(),
                    string.IsNullOrWhiteSpace(e.MainJob) ? null : e.MainJob.Trim(),
                    null,
                    string.IsNullOrWhiteSpace(e.SubJob) ? null : e.SubJob.Trim(),
                    null))
                .ToList();
            if (members.Count == 0)
            {
                return BadRequest(new { error = "At least one valid attendance entry is required." });
            }
            var submissionId = await approvalSvc.QueueAttendanceWindowAsync(
                eventEntity.LinkshellId,
                token.IssuedToAppUserId!,
                new AttendanceWindowSubmissionInput(eventId, request.WindowSequence ?? 1, members),
                cancellationToken);
            return Ok(new { pending = true, submissionId, queued = members.Count });
        }

        // Auto-commence the event if it hasn't started yet (so attendance has a meaningful base time).
        if (eventEntity.CommencementStartTime is null)
        {
            eventEntity.CommencementStartTime = nowUtc;
            eventEntity.StarterUserId ??= token.IssuedToAppUserId;
        }

        // For HNM events the addon may pin this batch to a specific spawn window
        // (1..GetWindowCount). Find or create the EventAttendanceWindow row up front
        // so per-user inserts below can attach to it.
        EventAttendanceWindow? attendanceWindow = null;
        if (request.WindowSequence is int windowSequence)
        {
            var maxWindows = eventEntity.WindowCountOverride ?? HnmConfig.GetWindowCount(eventEntity.EventName);
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
                    Label = HnmConfig.GetDefaultWindowLabel(eventEntity.EventName, windowSequence, maxWindows),
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
        var pendingLedgers = new List<AppUserEventStatusLedger>();
        // Per-attendee credit info so the addon can print a "+N DKP" line per
        // user immediately after a window post. Only populated for windowed
        // events (single-window events award DKP at end-of-event, not per post).
        var creditedAttendees = new List<object>();

        // Pre-load all linkshell memberships + their AppUser so we can index by
        // every name the player might be known by (the membership row's own
        // CharacterName, plus the AppUser's CharacterName / AltCharacterName1 /
        // AltCharacterName2). This makes the addon match work even when the
        // membership CharacterName was left null at pair time or when the player
        // logs in as one of their alts.
        var membershipsWithUser = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Where(m => m.LinkshellId == token.LinkshellId && m.AppUserId != null)
            .Join(_dbContext.Users.AsNoTracking(),
                  m => m.AppUserId,
                  u => u.Id,
                  (m, u) => new { Membership = m, User = u })
            .ToListAsync(cancellationToken);

        // Build the lookup by trying every candidate name. First-write-wins, so
        // the membership's own CharacterName takes precedence over alts when both
        // resolve to different members (rare but possible if two players share an
        // alt name).
        var membershipByName = new Dictionary<string, AppUserLinkshell>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in membershipsWithUser)
        {
            foreach (var candidate in new[]
                     {
                         pair.Membership.CharacterName,
                         pair.User.CharacterName,
                         pair.User.AltCharacterName1,
                         pair.User.AltCharacterName2,
                     })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var key = candidate.Trim();
                if (!membershipByName.ContainsKey(key))
                {
                    membershipByName[key] = pair.Membership;
                }
            }
        }

        // Self-identification backfill: when the addon tells us which character
        // it's currently logged in as, ensure the token issuer's membership and
        // AppUser both carry that name. This rescues the common signup path
        // where the user pairs the addon before ever filling out a character
        // name in the website profile — without it their own attendance posts
        // silently fall into `unmatched` because there's nothing to match
        // against. Tracked entities so EF persists the changes on SaveChanges
        // below; we re-read by AppUserId rather than reusing the AsNoTracking
        // copies above.
        if (!string.IsNullOrWhiteSpace(request.SelfCharacterName))
        {
            var selfName = request.SelfCharacterName.Trim();
            var issuerId = token.IssuedToAppUserId;

            var issuerMembership = await _dbContext.AppUserLinkshells
                .FirstOrDefaultAsync(
                    m => m.LinkshellId == token.LinkshellId && m.AppUserId == issuerId,
                    cancellationToken);
            var issuerUser = await _dbContext.Users
                .FirstOrDefaultAsync(u => u.Id == issuerId, cancellationToken);

            if (issuerMembership is not null)
            {
                if (string.IsNullOrWhiteSpace(issuerMembership.CharacterName))
                {
                    issuerMembership.CharacterName = selfName;
                }
                if (issuerUser is not null && string.IsNullOrWhiteSpace(issuerUser.CharacterName))
                {
                    issuerUser.CharacterName = selfName;
                }

                // Make sure the lookup picks up the freshly-known name even
                // though membershipsWithUser was loaded AsNoTracking — point
                // the dictionary at the (now-tracked) issuer membership so
                // downstream entry resolution finds it.
                if (!membershipByName.ContainsKey(selfName))
                {
                    membershipByName[selfName] = issuerMembership;
                }
            }
        }

        // Resolve every entry to a membership up-front so we can pre-load existing
        // participations and per-window credits in single queries instead of per-row.
        var resolvedEntries = new List<(AddonAttendanceEntry Entry, AppUserLinkshell Membership, string Name)>(request.Entries.Count);
        foreach (var entry in request.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.CharacterName)) continue;

            var name = entry.CharacterName.Trim();
            if (!membershipByName.TryGetValue(name, out var membership))
            {
                unmatched.Add(name);
                continue;
            }

            resolvedEntries.Add((entry, membership, name));
        }

        var resolvedAppUserIds = resolvedEntries
            .Select(r => r.Membership.AppUserId!)
            .Distinct()
            .ToList();

        var existingByAppUserId = resolvedAppUserIds.Count == 0
            ? new Dictionary<string, AppUserEvent>(StringComparer.Ordinal)
            : await _dbContext.AppUserEvents
                .Where(ue => ue.EventId == eventId && resolvedAppUserIds.Contains(ue.AppUserId!))
                .ToDictionaryAsync(ue => ue.AppUserId!, cancellationToken);

        // Pre-load all per-window credits for participants we already know about so the
        // "already attended this window" check becomes an in-memory lookup.
        var existingWindowCreditPairs = attendanceWindow is null || existingByAppUserId.Count == 0
            ? new HashSet<int>()
            : (await _dbContext.AppUserEventWindows
                .Where(w => w.EventAttendanceWindowId == attendanceWindow.Id
                    && existingByAppUserId.Values.Select(v => v.Id).Contains(w.AppUserEventId))
                .Select(w => w.AppUserEventId)
                .ToListAsync(cancellationToken))
                .ToHashSet();

        foreach (var (entry, membership, _) in resolvedEntries)
        {
            existingByAppUserId.TryGetValue(membership.AppUserId!, out var existing);

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
                existingByAppUserId[membership.AppUserId!] = participation;
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
                if (participation.Id != 0 && existingWindowCreditPairs.Contains(participation.Id))
                {
                    alreadyVerified++;
                    continue;
                }

                _dbContext.AppUserEventWindows.Add(new AppUserEventWindow
                {
                    AppUserEvent = participation,
                    EventAttendanceWindow = attendanceWindow,
                    VerifiedAt = nowUtc,
                    VerifiedBy = verifiedBy,
                    Zone = string.IsNullOrWhiteSpace(entry.Zone) ? null : entry.Zone.Trim()
                });

                creditedAttendees.Add(new
                {
                    characterName = participation.CharacterName,
                    jobName       = participation.JobName,
                    subJobName    = participation.SubJobName,
                    dkpEarned     = eventEntity.DkpPerHour ?? 0
                });
            }

            matched++;

            var ledger = new AppUserEventStatusLedger
            {
                AppUserEvent = participation,
                EventId = eventId,
                AppUserId = membership.AppUserId,
                ActionType = "Verify",
                OccurredAt = nowUtc,
                RequiresVerification = false,
                VerifiedAt = nowUtc,
                VerifiedBy = verifiedBy,
                Source = AddonSource,
                EventAttendanceWindow = attendanceWindow
            };
            _dbContext.AppUserEventStatusLedgers.Add(ledger);
            pendingLedgers.Add(ledger);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var ledgerIds = pendingLedgers.Select(l => l.Id).ToList();

        return Ok(new
        {
            matched,
            alreadyVerified,
            unmatched,
            ledgerEntryIds = ledgerIds,
            windowSequence = attendanceWindow?.SequenceNumber,
            windowId = attendanceWindow?.Id,
            windowLabel = attendanceWindow?.Label,
            dkpPerWindow = attendanceWindow is not null ? eventEntity.DkpPerHour : null,
            creditedAttendees
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
        if (!await TokenIssuerCanModerateAsync(token, attendanceWindow.Event.LinkshellId, cancellationToken))
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

    // Late-join: lets a regular member self-attach to an already-commenced
    // timed event from the addon, without needing an officer to scan and
    // post them. Mirrors the activity's ActivityDataController.QuickJoinAsync
    // contract — same fields, same "already attached" guard — but uses the
    // addon token to resolve the joining user and stamps IsVerified+ledger
    // so the new participation earns DKP from the join moment forward
    // (matching the officer-led PostAttendance path, not the looser
    // activity quick-join which leaves IsVerified null).
    [HttpPost("events/{eventId:int}/join")]
    [AddonApiAuth]
    public async Task<IActionResult> AddonJoinEventAsync(
        int eventId,
        [FromBody] AddonJoinEventRequest request,
        CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        if (string.IsNullOrWhiteSpace(token.IssuedToAppUserId))
        {
            return Unauthorized(new { error = "Token has no issuer; cannot self-join." });
        }

        if (string.IsNullOrWhiteSpace(request.MainJob) || string.IsNullOrWhiteSpace(request.SubJob))
        {
            return BadRequest(new { error = "Main job and sub job are required to join." });
        }

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

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Late join is only available after the event has started." });
        }

        // Windowed (HNM-style) events award DKP per posted window, not by
        // duration, so a self-join would create a no-op AppUserEvent that
        // never gets credited. Force officers to use the per-window Post
        // flow for those instead of confusing members with a join button
        // that silently doesn't pay out.
        var windowCount = eventEntity.WindowCountOverride ?? HnmConfig.GetWindowCount(eventEntity.EventName);
        if (windowCount > 1)
        {
            return BadRequest(new { error = "Late join only applies to timed events. Ask an officer to post a window for HNM-style events." });
        }

        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(
                m => m.LinkshellId == token.LinkshellId && m.AppUserId == token.IssuedToAppUserId,
                cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var existing = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(
                p => p.EventId == eventId && p.AppUserId == token.IssuedToAppUserId,
                cancellationToken);
        if (existing is not null)
        {
            return BadRequest(new { error = "You are already attached to this event." });
        }

        var nowUtc = DateTime.UtcNow;
        var characterName = membership.CharacterName;
        if (string.IsNullOrWhiteSpace(characterName))
        {
            var appUser = await _dbContext.Users
                .FirstOrDefaultAsync(u => u.Id == token.IssuedToAppUserId, cancellationToken);
            characterName = appUser?.CharacterName;
        }

        var participation = new AppUserEvent
        {
            AppUserId = token.IssuedToAppUserId,
            EventId = eventId,
            CharacterName = characterName,
            JobName = request.MainJob.Trim(),
            SubJobName = request.SubJob.Trim(),
            JobType = string.IsNullOrWhiteSpace(request.JobType) ? null : request.JobType.Trim(),
            StartTime = nowUtc,
            EventDkp = 0,
            IsQuickJoin = true,
            IsVerified = true
        };
        _dbContext.AppUserEvents.Add(participation);

        _dbContext.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
        {
            AppUserEvent = participation,
            EventId = eventId,
            AppUserId = token.IssuedToAppUserId,
            ActionType = "Verify",
            OccurredAt = nowUtc,
            RequiresVerification = false,
            VerifiedAt = nowUtc,
            VerifiedBy = (token.Label ?? "att-addon") + " (self-join)",
            Source = AddonSource
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            success = true,
            participationId = participation.Id,
            characterName = participation.CharacterName,
            jobName = participation.JobName,
            subJobName = participation.SubJobName,
            startTime = participation.StartTime
        });
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

        var memberRows = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == token.LinkshellId
                        && !string.IsNullOrWhiteSpace(link.CharacterName))
            .Select(link => new
            {
                CharacterName = link.CharacterName!,
                Alt1 = link.AppUser != null ? link.AppUser.AltCharacterName1 : null,
                Alt2 = link.AppUser != null ? link.AppUser.AltCharacterName2 : null,
                JobLevels = link.JobLevels
            })
            .OrderBy(row => row.CharacterName)
            .ToListAsync(cancellationToken);

        var characterNames = memberRows.Select(row => row.CharacterName).ToList();

        var roster = memberRows
            .Select(row =>
            {
                var alts = new List<string>(2);
                if (!string.IsNullOrWhiteSpace(row.Alt1)) alts.Add(row.Alt1!.Trim());
                if (!string.IsNullOrWhiteSpace(row.Alt2)) alts.Add(row.Alt2!.Trim());
                return new
                {
                    characterName = row.CharacterName,
                    alts,
                    jobLevels = row.JobLevels
                };
            })
            .ToList();

        return Ok(new
        {
            characterNames,
            roster,
            lootStructure = ActivityDataController.NormalizeLootStructure(linkshell?.LootStructure ?? "Dkp")
        });
    }

    // Stores the calling player's job-level array on their AppUserLinkshell
    // row so other addon installations can see (via /roster) which jobs each
    // member has leveled. Used by the in-game loot panel's "Can equip" filter.
    // Idempotent: overwrites the previous array on each call.
    [HttpPost("character/jobs")]
    [AddonApiAuth]
    public async Task<IActionResult> PostCharacterJobsAsync(
        [FromBody] AddonPostJobLevelsRequest request,
        CancellationToken cancellationToken)
    {
        var characterName = request.CharacterName?.Trim();
        if (string.IsNullOrWhiteSpace(characterName))
        {
            return BadRequest(new { error = "Character name is required." });
        }
        if (request.JobLevels is null || request.JobLevels.Length == 0)
        {
            return BadRequest(new { error = "JobLevels array is required." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        // Membership guard: a token can only publish job levels for a character
        // that is on its own linkshell roster. Match is case-insensitive on
        // CharacterName, mirroring the loot-winner validation pattern.
        var membership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.LinkshellId == token.LinkshellId
                && link.CharacterName != null
                && link.CharacterName.ToLower() == characterName!.ToLower(),
                cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        membership.JobLevels = request.JobLevels;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true, characterName = membership.CharacterName });
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
        if (tod is null)
        {
            return NotFound(new { error = "Tod not found." });
        }
        if (tod.LinkshellId != token.LinkshellId)
        {
            return Forbid();
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
        await _sheetSync.EnqueueAsync(tod.LinkshellId, cancellationToken);

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

        // Claim arrives verbatim on the request — tri-state:
        //   true  -> "Post (Claimed)" pressed in the ToD Capturing panel
        //   false -> "Post (Unclaimed)"
        //   null  -> ToD was auto-posted from the loot-pool flow before the
        //            user picked; the addon UI keeps the buttons live so they
        //            can settle it via an update call afterward.
        // No server-side guesswork — chat-line parsing and active-event
        // heuristics produced too many false negatives on real linkshells.
        var claimed = request.Claim;

        var role = await GetTokenIssuerRoleAsync(token, token.LinkshellId, cancellationToken);
        var canManage = role?.CanManageTods == true;
        var canSubmit = role?.CanSubmitTodForApproval == true;
        if (!canManage && !canSubmit)
        {
            return Forbid();
        }

        if (!canManage)
        {
            var approvalSvc = HttpContext.RequestServices.GetRequiredService<SubmissionApprovalService>();
            var input = new TodSubmissionInput(
                monsterName,
                null,
                claimed,
                defeatedAtUtc,
                cooldown,
                interval,
                repopTimeUtc,
                null,
                Array.Empty<TodSubmissionLootInput>());
            var submissionId = await approvalSvc.QueueTodAsync(token.LinkshellId, token.IssuedToAppUserId!, input, cancellationToken);
            return Ok(new { pending = true, submissionId, monsterName });
        }

        var tod = new Tod
        {
            LinkshellId = token.LinkshellId,
            MonsterName = monsterName,
            DayNumber = null,
            Claim = claimed,
            Time = defeatedAtUtc,
            Cooldown = cooldown,
            RepopTime = repopTimeUtc,
            Interval = interval,
            TimeStamp = nowUtc,
            TotalTods = 1,
            TotalClaims = claimed == true ? 1 : 0,
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
            interval = tod.Interval,
            claim = tod.Claim
        });
    }

    // Updates the claim status on an existing addon-posted ToD. The addon
    // UI calls this after the loot-pool flow auto-creates a ToD with claim
    // null (Not Specified) so the user can settle Claimed / Unclaimed
    // afterward without recreating the row.
    [HttpPost("tod/{todId:int}/claim")]
    [AddonApiAuth]
    public async Task<IActionResult> UpdateTodClaimAsync(
        int todId,
        [FromBody] AddonUpdateTodClaimRequest request,
        CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var tod = await _dbContext.Tods.FirstOrDefaultAsync(t => t.Id == todId, cancellationToken);
        if (tod is null)
        {
            return NotFound(new { error = "Tod not found." });
        }
        if (tod.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }

        tod.Claim = request.Claim;
        tod.TotalClaims = request.Claim == true ? 1 : 0;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { todId = tod.Id, claim = tod.Claim });
    }
}
