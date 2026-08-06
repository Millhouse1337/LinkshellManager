using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Manual Check In attendance driven from the LSM addon, mirroring the Discord camp
// board's "✅ Check In (this window)" / "🚪 Check Out" buttons so a member sitting at camp can
// check in from the game client instead of alt-tabbing to Discord.
//
// Both surfaces write the SAME AppUserEvent columns (WdArrivalWindow / WdDepartureWindow) on the
// SAME event row, so checking in on Discord and out in-game (or vice versa) is coherent and
// WdCampFinalizer still sees exactly one participation per member — no double credit.
//
// Identity here is the addon token's issuer (a real linked account), not a Discord snowflake:
// the addon can only check in the member who paired it, and only as a character that account
// actually owns (roster name or a registered alt). There is no addon equivalent of Discord's
// "outside member" onboarding — an unpaired player still uses the Discord button.
public sealed partial class AddonApiController
{
    // GET api/addon/wd-camps
    //
    // Every open Manual Check In camp on the token's linkshell, with the caller's own check-in
    // state per camp and the current window. The addon polls this to render its check-in panel
    // (which camps are live, which window they're on, whether you're already in).
    //
    // "Open" is exactly the Discord button's own guard: AttendanceMode == Wd and not yet
    // finalized. A popped camp stays listed through its Awaiting-Processing grace, because late
    // check-ins and corrections still count until WdCampFinalizer runs.
    [HttpGet("wd-camps")]
    [AddonApiAuth]
    public async Task<IActionResult> ListWdCampsAsync(CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var linkshell = await _dbContext.Linkshells
            .FirstOrDefaultAsync(ls => ls.Id == token.LinkshellId, cancellationToken);

        var identity = await ResolveTokenIssuerIdentityAsync(token, cancellationToken);

        var events = await _dbContext.Events
            .Where(evt => evt.LinkshellId == token.LinkshellId
                       && evt.AttendanceMode == HnmAttendanceModes.Wd
                       && evt.WdFinalizedAt == null)
            .ToListAsync(cancellationToken);

        var eventIds = events.Select(evt => evt.Id).ToList();
        var participations = eventIds.Count == 0
            ? new List<AppUserEvent>()
            : await _dbContext.AppUserEvents
                .Where(p => eventIds.Contains(p.EventId) && p.WdArrivalWindow != null)
                .ToListAsync(cancellationToken);

        var byEvent = participations
            .GroupBy(p => p.EventId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var camps = events
            .Select(evt =>
            {
                var attendees = byEvent.TryGetValue(evt.Id, out var rows) ? rows : new List<AppUserEvent>();
                var mine = FindOwnParticipation(attendees, identity);
                return new
                {
                    eventId = evt.Id,
                    name = evt.EventName,
                    monsterName = evt.AssignedMonsterName,
                    dayNumber = evt.DayNumber,
                    // The board's number, so the in-game panel, the Discord board, and the window
                    // a Check In actually records are all the same figure.
                    windowNumber = DiscordEventMessageBuilder.FocusWindow(evt),
                    openedWindowNumber = Math.Clamp(
                        evt.HnmWindowNumber, 1, DiscordEventMessageBuilder.EffectiveWindowCount(evt)),
                    windowCount = DiscordEventMessageBuilder.EffectiveWindowCount(evt),
                    nextWindowAt = evt.NextWindowAt,
                    // Popped and inside the finalize grace: still open to late check-ins, but the
                    // addon labels it so nobody thinks the camp is still running.
                    awaitingProcessing = evt.WdAwaitingProcessingSince != null,
                    started = evt.CommencementStartTime != null,
                    checkedIn = mine is not null,
                    arrivalWindow = mine?.WdArrivalWindow,
                    departureWindow = mine?.WdDepartureWindow,
                    attendeeCount = attendees.Count,
                    attendees = attendees
                        .Select(p => new
                        {
                            characterName = p.CharacterName,
                            arrivalWindow = p.WdArrivalWindow,
                            departureWindow = p.WdDepartureWindow,
                        })
                        .OrderBy(a => a.characterName)
                        .ToList(),
                };
            })
            // Camps you're already in float to the top, then the ones that have actually started,
            // then alphabetical — so the addon's first row is almost always the one you want.
            .OrderByDescending(c => c.checkedIn)
            .ThenByDescending(c => c.started)
            .ThenBy(c => c.name)
            .ToList();

        return Ok(new
        {
            attendanceMode = HnmAttendanceModes.Normalize(linkshell?.HnmAttendanceMode),
            // The character the addon would check in as when it doesn't send one (or sends a name
            // this account doesn't own). Null means the account has no roster name yet.
            characterName = identity?.Membership.CharacterName,
            camps
        });
    }

    // POST api/addon/events/{eventId}/checkin
    //
    // Records the caller's arrival window on this camp — the addon-side twin of the Discord
    // "✅ Check In (this window)" button (DiscordInteractionsController.HandleXinAsync). The
    // finalizer credits arrival..last window inclusive, so one check-in covers every later
    // window; checking in again on a later window overwrites the arrival (the late-arrival
    // correction) and clears any prior check-out.
    [HttpPost("events/{eventId:int}/checkin")]
    [AddonApiAuth]
    public async Task<IActionResult> WdCheckInAsync(
        int eventId,
        [FromBody] AddonWdCheckInRequest? request,
        CancellationToken cancellationToken)
    {
        var ctx = await ResolveWdCheckInContextAsync(eventId, request?.CharacterName, cancellationToken);
        if (ctx.Error is not null) return ctx.Error;

        var eventEntity = ctx.EventEntity!;
        var appUserId = ctx.AppUserId!;
        var characterName = ctx.CharacterName!;
        var windowCount = DiscordEventMessageBuilder.EffectiveWindowCount(eventEntity);
        // The board's number — see DiscordEventMessageBuilder.FocusWindow. Identical to the
        // Discord Check In button so the two surfaces record the same window for the same click.
        var arrivalWindow = DiscordEventMessageBuilder.FocusWindow(eventEntity);
        var openedWindow = Math.Clamp(eventEntity.HnmWindowNumber, 1, windowCount);
        var checkedInEarly = arrivalWindow > openedWindow;

        // First check-in makes the camp live (same as the Discord path), so the board reads
        // "Started" and the finalizer has a sensible StartTime.
        eventEntity.CommencementStartTime ??= DateTime.UtcNow;

        var existing = await FindParticipationAsync(eventId, appUserId, characterName, cancellationToken);
        if (existing is not null)
        {
            // Adopt an account-less row left by a Discord "outside member" check-in for this same
            // character, rather than adding a second row the board would show twice (and that the
            // finalizer would skip for having no ledger identity).
            existing.AppUserId ??= appUserId;
            existing.CharacterName = characterName;
            existing.WdArrivalWindow = arrivalWindow;
            existing.WdDepartureWindow = null;
            existing.IsVerified = true;
            existing.IsQuickJoin = true;
            existing.StartTime ??= eventEntity.CommencementStartTime;
        }
        else
        {
            _dbContext.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = appUserId,
                EventId = eventId,
                CharacterName = characterName,
                WdArrivalWindow = arrivalWindow,
                IsVerified = true,
                IsQuickJoin = true,
                EventDkp = 0,
                StartTime = eventEntity.CommencementStartTime,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _eventQueue.Enqueue(eventId); // re-render the Discord board so it shows the new attendee

        return Ok(new
        {
            eventId,
            eventName = eventEntity.EventName,
            characterName,
            arrivalWindow,
            windowNumber = arrivalWindow,
            windowCount,
            checkedInEarly,
            message = checkedInEarly
                ? $"Checked in before Window {arrivalWindow} opens - you'll get credit for Window {arrivalWindow} through the kill."
                : $"Checked in for Window {arrivalWindow} - you'll get credit for Window {arrivalWindow} through the kill."
        });
    }

    // POST api/addon/events/{eventId}/checkout
    //
    // The member is leaving mid-camp: records the departure window so credit stops there (they
    // keep arrival..departure inclusive). Twin of the Discord "🚪 Check Out" button. Checking in
    // again clears it.
    [HttpPost("events/{eventId:int}/checkout")]
    [AddonApiAuth]
    public async Task<IActionResult> WdCheckOutAsync(
        int eventId,
        [FromBody] AddonWdCheckInRequest? request,
        CancellationToken cancellationToken)
    {
        var ctx = await ResolveWdCheckInContextAsync(eventId, request?.CharacterName, cancellationToken);
        if (ctx.Error is not null) return ctx.Error;

        var eventEntity = ctx.EventEntity!;
        var existing = await FindParticipationAsync(eventId, ctx.AppUserId!, ctx.CharacterName!, cancellationToken);
        if (existing?.WdArrivalWindow is not { } arrival)
        {
            return BadRequest(new { error = "You're not checked in, so there's nothing to check out of." });
        }

        var windowCount = DiscordEventMessageBuilder.EffectiveWindowCount(eventEntity);
        // Same scale as check-in and the pop window: the board's number, floored at arrival so a
        // check-out can never land before the member checked in.
        var departureWindow = Math.Clamp(
            DiscordEventMessageBuilder.FocusWindow(eventEntity), arrival, windowCount);
        existing.WdDepartureWindow = departureWindow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        _eventQueue.Enqueue(eventId);

        return Ok(new
        {
            eventId,
            eventName = eventEntity.EventName,
            characterName = existing.CharacterName,
            arrivalWindow = arrival,
            departureWindow,
            windowCount,
            message = $"Checked out at Window {departureWindow} - you'll get credit for Windows {arrival} through {departureWindow}."
        });
    }

    // ---------------------------------------------------------------------
    // Shared resolution
    // ---------------------------------------------------------------------

    private sealed record WdCheckInContext(
        IActionResult? Error,
        Event? EventEntity = null,
        string? AppUserId = null,
        string? CharacterName = null);

    // The token issuer's linkshell membership plus the alt names their account owns, so a
    // check-in can be credited to the character actually sitting at camp.
    private sealed record TokenIssuerIdentity(AppUserLinkshell Membership, string? Alt1, string? Alt2);

    private async Task<TokenIssuerIdentity?> ResolveTokenIssuerIdentityAsync(
        AddonApiToken token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token.IssuedToAppUserId)) return null;

        var row = await _dbContext.AppUserLinkshells
            .Where(m => m.LinkshellId == token.LinkshellId && m.AppUserId == token.IssuedToAppUserId)
            .Select(m => new
            {
                Membership = m,
                Alt1 = m.AppUser != null ? m.AppUser.AltCharacterName1 : null,
                Alt2 = m.AppUser != null ? m.AppUser.AltCharacterName2 : null,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : new TokenIssuerIdentity(row.Membership, row.Alt1, row.Alt2);
    }

    private static bool SameCharacter(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    // The caller's row among a camp's attendees: by account first, then by a character name the
    // account owns (covers a row created from Discord before the account was linked).
    private static AppUserEvent? FindOwnParticipation(
        List<AppUserEvent> attendees, TokenIssuerIdentity? identity)
    {
        if (identity is null) return null;
        var appUserId = identity.Membership.AppUserId;
        var mine = attendees.FirstOrDefault(p =>
            p.AppUserId != null && string.Equals(p.AppUserId, appUserId, StringComparison.OrdinalIgnoreCase));
        if (mine is not null) return mine;

        return attendees.FirstOrDefault(p =>
            p.AppUserId == null
            && (SameCharacter(p.CharacterName, identity.Membership.CharacterName)
                || SameCharacter(p.CharacterName, identity.Alt1)
                || SameCharacter(p.CharacterName, identity.Alt2)));
    }

    private async Task<AppUserEvent?> FindParticipationAsync(
        int eventId, string appUserId, string characterName, CancellationToken cancellationToken)
    {
        var mine = await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(p => p.EventId == eventId && p.AppUserId == appUserId, cancellationToken);
        if (mine is not null) return mine;

        // Account-less row for the exact character we're checking in as. Character names are
        // unique per world and we've already verified this one belongs to the caller's account,
        // so claiming it can't attach someone else's attendance.
        var lowered = characterName.Trim().ToLower();
        return await _dbContext.AppUserEvents
            .FirstOrDefaultAsync(p => p.EventId == eventId
                                   && p.AppUserId == null
                                   && p.CharacterName != null
                                   && p.CharacterName.ToLower() == lowered, cancellationToken);
    }

    private async Task<WdCheckInContext> ResolveWdCheckInContextAsync(
        int eventId, string? requestedCharacterName, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var eventEntity = await _dbContext.Events
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return new WdCheckInContext(NotFound(new { error = "Event not found." }));
        }
        if (eventEntity.LinkshellId != token.LinkshellId)
        {
            return new WdCheckInContext(Forbid());
        }
        if (!DiscordEventMessageBuilder.IsWd(eventEntity))
        {
            return new WdCheckInContext(BadRequest(new { error = "This camp doesn't use Manual Check In." }));
        }
        if (eventEntity.WdFinalizedAt is not null)
        {
            return new WdCheckInContext(BadRequest(new { error = "This camp has already been processed — attendance is locked." }));
        }

        var identity = await ResolveTokenIssuerIdentityAsync(token, cancellationToken);
        if (identity is null)
        {
            // Either the pairing predates per-user issuance or the issuer left the linkshell.
            return new WdCheckInContext(new ObjectResult(new
            {
                error = "This pairing isn't linked to a member of this linkshell — re-pair the addon from your own account to check in."
            })
            { StatusCode = StatusCodes.Status403Forbidden });
        }

        // Which character gets the credit: the one the addon says is logged in, when the account
        // owns it (roster name or a registered alt) — that's the character actually at camp.
        // Anything else falls back to the roster name rather than crediting an unowned character.
        var requested = requestedCharacterName?.Trim();
        string? characterName = null;
        if (!string.IsNullOrWhiteSpace(requested)
            && (SameCharacter(requested, identity.Membership.CharacterName)
                || SameCharacter(requested, identity.Alt1)
                || SameCharacter(requested, identity.Alt2)))
        {
            characterName = requested;
        }
        characterName ??= identity.Membership.CharacterName?.Trim();

        // Membership never got a character name (blank at signup) and the addon knows exactly who
        // is logged in — backfill it, the same way the attendance post does.
        if (string.IsNullOrWhiteSpace(characterName) && !string.IsNullOrWhiteSpace(requested))
        {
            identity.Membership.CharacterName = requested;
            characterName = requested;
        }
        if (string.IsNullOrWhiteSpace(characterName))
        {
            return new WdCheckInContext(BadRequest(new
            {
                error = "Set your character name on your LSM profile before checking in."
            }));
        }

        return new WdCheckInContext(
            null, eventEntity, identity.Membership.AppUserId, characterName);
    }

    // Body for check-in / check-out. CharacterName is the addon's currently-logged-in character
    // (party memory slot 0); optional, and ignored unless the calling account owns it.
    public sealed record AddonWdCheckInRequest(string? CharacterName);
}
