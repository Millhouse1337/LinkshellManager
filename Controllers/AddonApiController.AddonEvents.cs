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
    [HttpGet("events")]
    [AddonApiAuth]
    public async Task<IActionResult> ListEventsAsync(CancellationToken cancellationToken)
    {
        var linkshellId = AddonApiAuthAttribute.GetLinkshellId(HttpContext);

        var query = _dbContext.Events
            .Where(evt => evt.LinkshellId == linkshellId);

        // Materialized as ENTITIES (not a projection) so the shared window helpers below can be
        // called on them. DiscordEventMessageBuilder.FocusWindow / EffectiveWindowCount need the
        // same popped / defeated / finalized sentinels the board checks, and re-deriving any of
        // that here is exactly how the addon drifted out of sync with the board in the first
        // place. Capped at 50 rows, so materializing is cheap.
        var raw = await query
            .OrderByDescending(evt => evt.CommencementStartTime)
            .ThenByDescending(evt => evt.StartTime)
            .Take(50)
            .ToListAsync(cancellationToken);

        // One row, for openedWindowDkp below. The addon has no way to resolve a camp's own pricing
        // (Event.Hnm*Override ?? the linkshell's HNM settings, branched on the attendance mode),
        // and every past attempt to let a client re-derive server state is what the comment above
        // is about — so the number is computed here and sent down finished.
        var linkshell = await _dbContext.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);

        // This linkshell's ENABLED Repeat-on-ToD leads, keyed by lower-cased monster, so each camp
        // below can report whether ending it is supposed to be followed by a re-posted board.
        //
        // The addon needs this to know that a ToD is REQUIRED before End Event: a recurring camp
        // ended with no ToD logged leaves the standing board with nothing to count back from, so
        // it silently never re-posts. Knowing the lead as well lets the addon say when the board
        // is coming back before the officer commits.
        var repeatLeadByMonster = (await _dbContext.HnmRecurringBoards
                .AsNoTracking()
                .Where(b => b.LinkshellId == linkshellId && b.Enabled)
                .Select(b => new { b.MonsterName, b.LeadHours })
                .ToListAsync(cancellationToken))
            .GroupBy(b => (b.MonsterName ?? string.Empty).Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().LeadHours);
        // Matched on every spelling of the spawn (either half of a merge pair and the combined
        // "Base/Stronger" label), the same rule HnmRecurringBoardService.FindAsync applies.
        double? RepeatLeadFor(string? monsterName)
        {
            foreach (var name in HnmConfig.MonsterMatchNamesLower(monsterName))
            {
                if (repeatLeadByMonster.TryGetValue(name, out var lead))
                {
                    return lead;
                }
            }
            return null;
        }

        // The linkshell's CONFIGURED cooldown for each camp's monster, in fractional hours. Sent so
        // the addon can show the repop a captured ToD is about to produce ("dies now → pops at X →
        // board back at X − lead") before the officer confirms, instead of only learning it from
        // the POST /api/addon/tod response after the fact.
        //
        // Resolved through the same MonsterTimingResolver → ResolveTodCooldownHours path
        // HnmCampPopService uses, so the addon's preview and the number the server later stores
        // come from one source. One lookup per DISTINCT monster, not per event.
        var cooldownHoursByMonster = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var monster in raw
                     .Select(evt => evt.AssignedMonsterName?.Trim())
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Select(name => name!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cooldown = await ActivityDataController.GetDefaultTodCooldownAsync(
                _monsterTimings, linkshellId, monster, cancellationToken);
            cooldownHoursByMonster[monster] = ActivityDataController.ResolveTodCooldownHours(cooldown);
        }
        double? CooldownHoursFor(string? monsterName)
        {
            var monster = monsterName?.Trim();
            return !string.IsNullOrWhiteSpace(monster)
                && cooldownHoursByMonster.TryGetValue(monster, out var hours)
                && hours > 0
                    ? hours
                    : (double?)null;
        }

        // createdFromAddon is sourced from the explicit CreationSource column. Legacy
        // rows (CreationSource null) fall back to a Details-prefix check so events
        // created before the column existed still show the cancel button if they
        // were originally posted by the addon.
        var events = raw.Select(evt => new
        {
            id = evt.Id,
            name = evt.EventName,
            type = evt.EventType,
            location = evt.EventLocation,
            startTime = evt.StartTime,
            commencementStartTime = evt.CommencementStartTime,
            isLive = evt.CommencementStartTime != null && evt.EndTime == null,
            dkpPerHour = evt.DkpPerHour,
            // The SPAWN window count — how many pop chances the camp sits through. Same helper the
            // board uses, so "of M" can't disagree with it. This is the scale hnmWindowNumber /
            // openedWindowNumber below are on, and what the addon clamps them against.
            windowCount = DiscordEventMessageBuilder.EffectiveWindowCount(evt),
            // How many attendance POSTS this camp takes, which is a different number: a Standard
            // king/dragon camp reads the roster twice (Open + Close) across those 7 spawn windows.
            // It decides how many Post buttons the addon offers and whether it NAMES its windows
            // (Open / Close) or numbers them — constants.window_label mirrors the same rule.
            //
            // Sent as its own field because the addon used to take the count above for both jobs.
            // That worked only while the two happened to be equal, and stopped the moment the board
            // started traversing the real spawn cycle: a Standard Behemoth camp began asking its
            // officer for seven posts instead of an Open and a Close.
            attendancePostCount = DiscordEventMessageBuilder.AttendancePostCount(evt),
            // Whether the Break Room applies (take break / force break / return / verify / deny).
            // False for windowed HNM camps: they credit per posted window, so there is no timer
            // to pause. Same predicate the break endpoints enforce, so the addon can't offer a
            // button the server would refuse — see events_sync.break_room_applies.
            supportsBreakRoom = EventBreakPolicy.SupportsBreakRoom(evt),
            assignedMonsterName = evt.AssignedMonsterName,
            // DISPLAY ONLY — the window number to SHOW, whatever the board prints. Always
            // FocusWindow, never the raw HnmWindowNumber column: closing that gap is exactly the
            // "board says 18, addon says 17" mismatch this exists to prevent.
            //
            // This is the window being AWAITED — one past openedWindowNumber — on every windowed
            // camp with a next window, wyrms included (see FocusWindow; the wyrms were excepted
            // here until the heading was found naming a window that had already passed). It
            // collapses onto the opened window only when there is no next one: final window,
            // popped, defeated, finalized.
            //
            // Do NOT post attendance against this. See openedWindowNumber below.
            hnmWindowNumber = DiscordEventMessageBuilder.FocusWindow(evt),
            // THE window number to POST ATTENDANCE AGAINST: the window that is actually open
            // right now. Window N opens at WindowAnchorAt + (N-1)×cadence, so window 1 IS the
            // camp's start / repop time — the opener.
            //
            // Attendance must be on this scale, not the awaited one. A snapshot records who was
            // present during a window, and posting it one ahead meant sequence 1 was never
            // written at all — which silently made HnmStandardCampFinalizer's `windows.Contains(1)`
            // open-bonus test unreachable on every monster with a timed cadence.
            // Manual Check In check-in records arrival against this too, so both models share one scale.
            openedWindowNumber = Math.Clamp(
                evt.HnmWindowNumber, 1, DiscordEventMessageBuilder.EffectiveWindowCount(evt)),
            // What ONE MORE snapshot, posted right now against openedWindowNumber, would be worth
            // per attendee under THIS camp's own pricing. The addon seeds its "Dkp this window"
            // box from it and displays nothing at all when it is null — "this camp does not price
            // windows" is a real answer, and a local default in its place is what produced
            // "+1 DKP each" on a camp the app pays 0 for.
            //
            // STABLE, as of the explicit close. It used to be a prediction that moved — posting
            // window N made N the close, so window 1 quoted open+close and dropped to open once a
            // later window landed — and because the addon writes its quote back as the window's
            // explicit price, every window kept the close bonus it had briefly been quoted. The
            // close is an officer's checkbox now, so this quotes the open on window 1 and the
            // regular window rate everywhere else, and what is quoted is what is paid.
            openedWindowDkp = HnmCampPricing.DefaultWindowValue(
                evt, linkshell,
                Math.Clamp(evt.HnmWindowNumber, 1, DiscordEventMessageBuilder.EffectiveWindowCount(evt))),
            // When the next window opens (null on the final window or an untimed monster),
            // so the addon can show the same live countdown the board does.
            nextWindowAt = evt.NextWindowAt,
            // What window 1 was anchored to. Re-stamped when an officer steps the window
            // manually, which is why the addon must not re-derive the window from StartTime.
            windowAnchorAt = evt.WindowAnchorAt,
            createdFromAddon = evt.CreationSource == "Addon"
                || (evt.CreationSource is null
                    && (evt.Details ?? string.Empty)
                        .StartsWith("Created from addon.", StringComparison.Ordinal)),
            // Whether this camp's monster has an ENABLED standing signup board, and how many hours
            // before the next predicted pop it re-posts. Both null/false for a non-HNM event and
            // for a monster with recurrence switched off.
            //
            // These drive the addon's End Event guard: on a recurring camp it will not end without
            // a Time of Death, because the ToD is the only thing the re-post can be scheduled from.
            repeatOnTod = RepeatLeadFor(evt.AssignedMonsterName) is not null,
            repeatLeadHours = RepeatLeadFor(evt.AssignedMonsterName),
            // The board's "Day N" label, so a ToD the addon posts while ending the camp is filed
            // under the same day the board is printing. Without it a day-cycle monster's next
            // board would come back bare ("Nidhogg" rather than "Nidhogg D3").
            dayNumber = evt.DayNumber,
            // The monster's configured cooldown in fractional hours, for the addon's
            // "pops at / board back at" preview. Null when the monster has none configured.
            todCooldownHours = CooldownHoursFor(evt.AssignedMonsterName)
        });

        return Ok(new { events });
    }

    // GET /api/addon/party-setups
    //
    // Reusable party-setup templates for the token's linkshell, for the
    // addon's "Party setup" picker on the event presets. Attaching one to an
    // event is what gives its Discord post a sign-up board.
    //
    // Templates only: rows with OwnerEventId set are per-event snapshots
    // cloned when someone edits a live board, and are meaningless as choices.
    // Read-only, so any paired member can list them -- only creating the event
    // requires moderation rights.
    [HttpGet("party-setups")]
    [AddonApiAuth]
    public async Task<IActionResult> ListPartySetupsAsync(CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var setups = await _dbContext.PartySetups
            .AsNoTracking()
            .Where(p => p.LinkshellId == token.LinkshellId && p.OwnerEventId == null)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                eventType = p.EventType,
                assignedMonsterName = p.AssignedMonsterName
            })
            .ToListAsync(cancellationToken);

        return Ok(new { partySetups = setups });
    }

    [HttpPost("events")]
    [AddonApiAuth]
    public async Task<IActionResult> CreateEventAsync(
        [FromBody] AddonCreateEventRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Event name is required." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        if (!await TokenIssuerCanModerateAsync(token, token.LinkshellId, cancellationToken))
        {
            return Forbid();
        }

        var nowUtc = DateTime.UtcNow;

        // Attach a party-setup template if the addon asked for one. Two guards,
        // matching the website's create path:
        //   * it must belong to THIS linkshell (cross-linkshell defense)
        //   * it must be a reusable template, not another event's private
        //     snapshot (OwnerEventId != null) -- sharing a snapshot would let
        //     two events edit the same board
        // A setup that fails either check is dropped rather than rejected, so a
        // stale id in the addon's cached list can't block event creation.
        int? partySetupId = null;
        if (request.PartySetupId is int requestedSetupId)
        {
            var setupIsUsable = await _dbContext.PartySetups
                .AsNoTracking()
                .AnyAsync(p => p.Id == requestedSetupId
                               && p.LinkshellId == token.LinkshellId
                               && p.OwnerEventId == null,
                          cancellationToken);
            if (setupIsUsable) partySetupId = requestedSetupId;
        }

        // Note: CommencementStartTime is intentionally null on create so the event
        // appears as "Queued". Callers that want to start immediately should follow
        // up with POST /api/addon/events/{id}/start.
        var eventEntity = new Event
        {
            PartySetupId = partySetupId,
            LinkshellId = token.LinkshellId,
            EventName = request.Name.Trim(),
            EventType = string.IsNullOrWhiteSpace(request.Type) ? null : request.Type.Trim(),
            EventLocation = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim(),
            CreatorUserId = token.IssuedToAppUserId,
            StartTime = request.StartUtc ?? nowUtc,
            DkpPerHour = request.DkpPerHour,
            Details = string.IsNullOrWhiteSpace(request.Details) ? "Created from lsm addon." : request.Details.Trim(),
            CreationSource = "Addon",
            // Persist explicit window-count override only when the addon
            // actually picked a multi-post style. Storing >=2 keeps the
            // column meaningful (1 = default, no need to record).
            WindowCountOverride = request.WindowCount is > 1 ? request.WindowCount : null,
            TimeStamp = nowUtc
        };

        // HNM camps carry a monster and a day as REAL columns, not just as text inside the
        // name. Without them an addon-made camp opened blank in the web Edit form, the sign-up
        // board couldn't pick the right half of a merge pair, and End Camp refused outright
        // ("this HNM camp has no monster assigned").
        if (string.Equals(eventEntity.EventType, "HNM", StringComparison.OrdinalIgnoreCase))
        {
            var (derivedMonster, derivedDay) = DeriveHnmFromEventName(eventEntity.EventName);
            var monster = TrimToNullString(request.MonsterName) ?? derivedMonster;
            eventEntity.AssignedMonsterName = monster;
            eventEntity.DayNumber = request.DayNumber ?? derivedDay;

            // The addon sends the officer's current zone; fall back to the monster's canonical
            // camp so the board still says where to go when it doesn't.
            if (string.IsNullOrWhiteSpace(eventEntity.EventLocation))
            {
                eventEntity.EventLocation = HnmConfig.ZoneFor(monster);
            }

            // Only the Manual Check In stamp, NOT HnmEventSeeder.SeedHnmEventAsync: that also writes
            // the monster's SPAWN window count into WindowCountOverride, and this camp's override is
            // already holding the other number — the count of attendance POSTS the officer takes
            // (Open + Close = 2 on a king/dragon, one per window on a wyrm), which is what that column
            // is for. Both numbers matter and they are not the same: the 7 spawn windows are pop
            // chances the camp sits through, while a Standard camp reads the roster twice.
            //
            // The spawn count doesn't need storing anyway — DiscordEventMessageBuilder.EffectiveWindowCount
            // derives it from the monster's built-in cadence, so the board still traverses all 7 while
            // the addon still posts Open / Close off this column.
            eventEntity.AttendanceMode = HnmEventSeeder.ResolveMode(
                await _dbContext.Linkshells
                    .Where(l => l.Id == token.LinkshellId)
                    .Select(l => l.HnmAttendanceMode)
                    .FirstOrDefaultAsync(cancellationToken),
                monster);
        }

        _dbContext.Events.Add(eventEntity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            eventId = eventEntity.Id,
            name = eventEntity.EventName,
            commencementStartTime = eventEntity.CommencementStartTime,
            assignedMonsterName = eventEntity.AssignedMonsterName,
            dayNumber = eventEntity.DayNumber
        });
    }

    private static string? TrimToNullString(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Reads the monster and day back out of a camp name the addon built, e.g.
    // "Behemoth/King Behemoth D1" -> ("Behemoth/King Behemoth", 1).
    //
    // The fallback for addon builds that predate sending them as fields. Only returns a monster
    // the catalog actually recognises, so an ad-hoc camp ("Goblin Furrier test") doesn't get a
    // made-up monster stamped on it from its own title.
    private static (string? Monster, int? Day) DeriveHnmFromEventName(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return (null, null);

        var name = eventName.Trim();
        int? day = null;
        var match = System.Text.RegularExpressions.Regex.Match(
            name, @"^(?<base>.*?)\s+D(?<day>\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            name = match.Groups["base"].Value.Trim();
            if (int.TryParse(match.Groups["day"].Value, out var parsedDay) && parsedDay > 0)
            {
                day = parsedDay;
            }
        }

        return (HnmConfig.IsTrueHnm(name) ? name : null, day);
    }

    // PATCH /api/addon/events/{eventId}
    //
    // Updates a live or queued event's DKP rate from the addon. The intended
    // use case: an officer has an HNM event running, opens the att Settings
    // popup, changes "HNM - DKP / Window" from 1 to 5, and clicks Save —
    // the next window posted should credit 5 DKP, not the value the event
    // was created with. The PostAttendance flow reads eventEntity.DkpPerHour
    // when crediting per-window DKP, so the simplest path is to update that
    // column whenever the user explicitly saves new defaults.
    //
    // The single DkpPerHour column is reused for both event flavors — the
    // addon picks which of its two defaults (dkpPerHourRegular vs
    // dkpPerWindowHnm) to send based on the event's window count.
    //
    // Idempotent: setting the same value is a no-op and returns 200.
    [HttpPatch("events/{eventId:int}")]
    [AddonApiAuth]
    public async Task<IActionResult> UpdateEventAsync(
        int eventId,
        [FromBody] AddonUpdateEventRequest request,
        CancellationToken cancellationToken)
    {
        if (request.DkpPerHour is null)
        {
            return BadRequest(new { error = "dkpPerHour is required." });
        }
        if (request.DkpPerHour < 0)
        {
            return BadRequest(new { error = "dkpPerHour must be non-negative." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);

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
        if (!await TokenIssuerCanModerateAsync(token, eventEntity.LinkshellId, cancellationToken))
        {
            return Forbid();
        }
        if (eventEntity.EndTime is not null)
        {
            return BadRequest(new { error = "Cannot edit a closed event." });
        }

        eventEntity.DkpPerHour = request.DkpPerHour;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            id = eventEntity.Id,
            dkpPerHour = eventEntity.DkpPerHour
        });
    }

    public sealed record AddonUpdateEventRequest(int? DkpPerHour);

    [HttpDelete("events/{eventId:int}")]
    [AddonApiAuth]
    public async Task<IActionResult> CancelEventAsync(int eventId, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var eventToDelete = await _dbContext.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.EventLootDetails)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventToDelete is null)
        {
            return NotFound(new { error = "Event not found." });
        }
        if (eventToDelete.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }
        if (!await TokenIssuerCanModerateAsync(token, eventToDelete.LinkshellId, cancellationToken))
        {
            return Forbid();
        }
        if (eventToDelete.CommencementStartTime.HasValue)
        {
            return BadRequest(new { error = "Live events cannot be canceled. End the event instead." });
        }

        _dbContext.AppUserEvents.RemoveRange(eventToDelete.AppUserEvents);
        _dbContext.EventLootDetails.RemoveRange(eventToDelete.EventLootDetails);
        _dbContext.Events.Remove(eventToDelete);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { canceled = eventId });
    }

    [HttpPost("events/{eventId:int}/start")]
    [AddonApiAuth]
    public async Task<IActionResult> StartEventAsync(int eventId, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var eventEntity = await _dbContext.Events
            .Include(evt => evt.AppUserEvents)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "Event not found." });
        }
        if (eventEntity.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }
        if (!await TokenIssuerCanModerateAsync(token, eventEntity.LinkshellId, cancellationToken))
        {
            return Forbid();
        }

        var alreadyStarted = eventEntity.CommencementStartTime is not null;
        if (!alreadyStarted)
        {
            eventEntity.CommencementStartTime = DateTime.UtcNow;
            eventEntity.StarterUserId ??= token.IssuedToAppUserId;
        }

        // Anchor window 1 to the SCHEDULED StartTime, mirroring the Activity's start
        // (ActivityDataController.EventsLifecycle) — starting a camp early by hand must not shift its
        // cadence. The advance poller falls back to StartTime while this is null, so the counter moved
        // either way, but the anchor is also what this controller hands back to the addon
        // (windowAnchorAt) and what a manual Prev/Next re-anchors against, so an addon start has to
        // stamp it too or those two disagree with the board.
        if (string.Equals((eventEntity.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase))
        {
            eventEntity.WindowAnchorAt ??= eventEntity.StartTime;
        }

        // Addon-started events must materialize event-board signups too, or those
        // members only exist on the party board and never reach EventHistory.
        await EventPartySignupService.MaterializeSignupsAsParticipantsAsync(_dbContext, eventEntity, cancellationToken);
        foreach (var participation in eventEntity.AppUserEvents)
        {
            participation.StartTime ??= eventEntity.CommencementStartTime;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            eventId = eventEntity.Id,
            name = eventEntity.EventName,
            commencementStartTime = eventEntity.CommencementStartTime,
            alreadyStarted
        });
    }

    // Ends a running event from the addon. Mirrors the web app's EndEvent
    // flow (writes EventHistory + DkpLedgerEntry rows, removes the live
    // Event / Jobs / participants / loot details). Reuses the same internal
    // helper so the DKP math stays in lockstep with the web path.
    [HttpPost("events/{eventId:int}/end")]
    [AddonApiAuth]
    public async Task<IActionResult> EndEventAsync(int eventId, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var eventEntity = await _dbContext.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.EventLootDetails)
            .FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);

        if (eventEntity is null)
        {
            return NotFound(new { error = "Event not found." });
        }
        if (eventEntity.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }
        if (!await TokenIssuerCanModerateAsync(token, eventEntity.LinkshellId, cancellationToken))
        {
            return Forbid();
        }

        var result = await EventController.EndEventCoreAsync(_dbContext, _dkpLedger, _dkpPools, eventEntity);
        var windowCount = eventEntity.WindowCountOverride ?? HnmConfig.GetWindowCount(eventEntity.EventName);

        // For windowed events DkpPerHour is reused as DkpPerWindow (same column,
        // different semantic). Surface both names so the addon can format the
        // chat output correctly without inferring from windowCount alone.
        return Ok(new
        {
            eventId               = eventId,
            eventName             = eventEntity.EventName,
            eventType             = eventEntity.EventType,
            eventLocation         = eventEntity.EventLocation,
            commencementStartTime = eventEntity.CommencementStartTime,
            endTime               = result.EndTimeUtc,
            dkpPerHour            = result.WindowCount > 1 ? (int?)null : eventEntity.DkpPerHour,
            dkpPerWindow          = result.WindowCount > 1 ? eventEntity.DkpPerHour : (int?)null,
            windowCount           = result.WindowCount,
            participants          = result.Participants.Select(p => new
            {
                characterName    = p.CharacterName,
                jobName          = p.JobName,
                subJobName       = p.SubJobName,
                durationHours    = p.DurationHours,
                windowsAttended  = p.WindowsAttended,
                dkpEarned        = p.DkpEarned
            })
        });
    }

    // Returns one event (with its posted attendance windows + per-window attendees)
    // so the addon can rehydrate its in-memory window cache after a reload. Cheaper
    // than re-fetching all events when the user just wants to re-select one.
    [HttpGet("events/{eventId:int}")]
    [AddonApiAuth]
    public async Task<IActionResult> GetEventAsync(int eventId, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var eventEntity = await _dbContext.Events
            .Include(e => e.AttendanceWindows)
                .ThenInclude(w => w.Attendees)
                    .ThenInclude(a => a.AppUserEvent)
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);

        if (eventEntity is null) return NotFound(new { error = "Event not found." });
        if (eventEntity.LinkshellId != token.LinkshellId) return Forbid();

        var linkshell = await _dbContext.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == eventEntity.LinkshellId, cancellationToken);

        // Resolved ONCE against the windows already posted, exactly as the finalizer would if the
        // camp ended now. This is what lets a re-selected camp rehydrate its frozen tabs with the
        // server's own numbers instead of re-deriving them from Event.DkpPerHour — the column the
        // addon itself used to write and then read back as if it were a payout.
        var closeWindow = HnmStandardCampFinalizer.ResolveCloseWindow(
            eventEntity.AttendanceWindows, eventEntity.HnmWindowNumber);

        return Ok(new
        {
            id = eventEntity.Id,
            name = eventEntity.EventName,
            type = eventEntity.EventType,
            location = eventEntity.EventLocation,
            startTime = eventEntity.StartTime,
            commencementStartTime = eventEntity.CommencementStartTime,
            isLive = eventEntity.CommencementStartTime != null && eventEntity.EndTime == null,
            windowCount = eventEntity.WindowCountOverride ?? HnmConfig.GetWindowCount(eventEntity.EventName),
            windows = eventEntity.AttendanceWindows
                .OrderBy(w => w.SequenceNumber)
                .Select(w => new
                {
                    sequenceNumber = w.SequenceNumber,
                    label = w.Label,
                    postedAt = w.PostedAt,
                    // The officer's explicit price for this window (null = none), and what it
                    // actually pays each attendee once the camp's defaults are applied.
                    dkpAmount = w.DkpAmount,
                    dkpValue = HnmCampPricing.WindowValueFor(
                        eventEntity, linkshell, w.SequenceNumber, closeWindow, w.DkpAmount,
                        w.IsKillWindow),
                    // So the addon can tick the right tab and label the kill roster as its own
                    // read rather than as "window N" — see constants.window_label.
                    isClosingWindow = w.IsClosingWindow,
                    isKillWindow = w.IsKillWindow,
                    // The DENORMALIZED name on the snapshot row leads, falling back to the
                    // participation for rows written before it was stamped. The participation is
                    // deleted by a roster clear (the wyrm camps wipe every window) while the
                    // snapshot survives it via SetNull — which is the whole reason
                    // AppUserEventWindow.CharacterName exists. Reading the name only through the
                    // navigation handed the addon a null for exactly those rows, and it rendered
                    // them as blank entries whose Remove button posted an empty name. It is also
                    // the only row that knows WHICH character was scanned, so a player on an alt
                    // rehydrates as the alt here — with mainCharacterName carrying the "(alt of X)"
                    // note the addon already prints from its own live scan. Jobs have no such
                    // fallback and stay null; the addon shows its unknown-jobs placeholder.
                    attendees = w.Attendees
                        .OrderBy(a => a.CharacterName ?? (a.AppUserEvent != null ? a.AppUserEvent.CharacterName : null))
                        .Select(a => new
                        {
                            id = a.Id,
                            characterName = a.CharacterName ?? (a.AppUserEvent != null ? a.AppUserEvent.CharacterName : null),
                            mainCharacterName = a.MainCharacterName,
                            jobName = a.AppUserEvent != null ? a.AppUserEvent.JobName : null,
                            subJobName = a.AppUserEvent != null ? a.AppUserEvent.SubJobName : null,
                            zone = a.Zone,
                            // Which alliance posted this attendee. Null on rows written before the
                            // window path carried one -- the addon buckets those as "unknown"
                            // rather than assuming alliance 1.
                            allianceNumber = a.AllianceNumber,
                            allianceKey = a.AllianceKey,
                            verifiedAt = a.VerifiedAt
                        })
                        .ToList()
                })
                .ToList()
        });
    }
}
