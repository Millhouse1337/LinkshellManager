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
    [HttpPost("events/{eventId:int}/attendance")]
    [AddonApiAuth]
    public Task<IActionResult> PostAttendanceAsync(
        int eventId,
        [FromBody] AddonAttendanceRequest request,
        CancellationToken cancellationToken) =>
        // Two officers posting the same window at once: the loser re-runs and adds only who is left.
        RetryIfWindowCreditRaceAsync(
            _dbContext, () => PostAttendanceCoreAsync(eventId, request, cancellationToken));

    // How far the officer's PC clock may be from the server's before its "recorded at" is ignored.
    public static readonly TimeSpan PostClockTolerance = TimeSpan.FromMinutes(5);

    // The time a window post is stamped with. The addon sends its PC clock's "now", which was taken
    // as-is: a PC a day off stamped the window (and a camp it started) a day off. A post is sent the
    // moment it is clicked, so a time more than a few minutes from the server's own is a wrong
    // clock, and the server's time is used instead. Same idea as the snapshot route's check.
    internal static DateTime TrustedPostTimeUtc(DateTime? sentUtc, DateTime nowUtc)
    {
        if (sentUtc is { } sent
            && sent >= nowUtc - PostClockTolerance
            && sent <= nowUtc + PostClockTolerance)
        {
            return sent;
        }
        return nowUtc;
    }

    private async Task<IActionResult> PostAttendanceCoreAsync(
        int eventId, AddonAttendanceRequest request, CancellationToken cancellationToken)
    {
        if (request.Entries is null || request.Entries.Count == 0)
        {
            return BadRequest(new { error = "At least one attendance entry is required." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = TrustedPostTimeUtc(request.RecordedAtUtc, DateTime.UtcNow);

        // Which alliance this roster is FOR, resolved ONLY when the addon actually sent one.
        //
        // AttendanceSnapshotAlliances.Resolve(null) returns 1, so calling it unconditionally would
        // stamp every post from an addon that predates this as "alliance 1" -- inventing a fact
        // nobody reported, and exactly the trap AttendanceSnapshot.AllianceNumber's own comment
        // warns about. Null has to stay null.
        var postedAllianceNumber = request.AllianceNumber.HasValue
            ? AttendanceSnapshotAlliances.Resolve(request.AllianceNumber)
            : (int?)null;
        var postedAllianceKey = string.IsNullOrWhiteSpace(request.AllianceKey)
            ? null
            : request.AllianceKey.Trim();

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

        // Posting a window roster needs "Manage attendance with the addon", checked HERE and not
        // just by the addon hiding its buttons -- the addon caches this permission from /me for up
        // to 60s, and a cache is not a permission boundary. There is no queue to fall back to: a
        // member without the permission counts attendance with `/lsm now`, which lands Pending in
        // Unlinked Snapshots for an officer to confirm.
        if (!await TokenIssuerCanModerateAsync(token, eventEntity.LinkshellId, cancellationToken))
        {
            return Forbid();
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
            // The window row: found, or created on the first post to it. Shared with the Activity's
            // Add New Post (AddonApiController.WindowPosts) so the range check and the "the Close
            // has been posted" guard are one rule.
            var (foundWindow, windowError) = await FindOrCreateAttendanceWindowAsync(
                eventEntity, windowSequence, request.IsKillWindow, nowUtc, AddonSource, cancellationToken);
            if (windowError is not null)
            {
                return BadRequest(new { error = windowError });
            }
            attendanceWindow = foundWindow!;   // no error means a row came back

            // What the officer typed into "Dkp this window" before pressing Post. It rides on the
            // post rather than a separate PATCH because THIS request is what creates the window
            // row — pricing it anywhere else leaves a moment where the window exists at the wrong
            // price, and the addon used to bridge that gap by PATCHing Event.DkpPerHour, a column
            // this camp's payout ignores entirely.
            //
            // Applied on the backfill branch too, so re-posting into an existing window can
            // correct its price. Null means "leave it alone", which is what the ARMED automatic
            // capture always sends — nobody typed a rate.
            //
            // Never applied to a kill roster. That window is worth 0 as a window and pays through
            // the kill bonus (HnmStandardCampFinalizer.WindowValue); an explicit amount would
            // replace the 0 and pay the late arrivals twice for one appearance.
            if (request.WindowDkp is { } typedWindowDkp
                && !attendanceWindow.IsKillWindow
                && !double.IsNaN(typedWindowDkp)
                && !double.IsInfinity(typedWindowDkp)
                && typedWindowDkp >= 0
                && HnmCampPricing.HonoursWindowAmount(eventEntity))
            {
                // Fetched inside the guard rather than Include'd on the event above: this is the
                // one branch that needs it, and it only runs when an officer actually typed a rate.
                var increment = await _dbContext.Linkshells
                    .Where(l => l.Id == eventEntity.LinkshellId)
                    .Select(l => l.DkpRoundingIncrement)
                    .FirstOrDefaultAsync(cancellationToken);
                attendanceWindow.DkpAmount =
                    DkpRounding.Round(typedWindowDkp, DkpRounding.StepFor(increment));
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        // The same text every addon post has always stored (tokens never had a label to put first).
        const string verifiedBy = "lsm-addon (lsm)";

        // What one window is actually worth, for the "+N DKP" line the addon prints after a post.
        // Null when the number would be fiction, and the addon suppresses the whole block.
        //
        // An EXPLICITLY-PRICED window pays exactly that per attendee even on a Standard camp, so
        // report it — that number is stored on the window and HnmStandardCampFinalizer will pay it.
        //
        // A Standard-mode HNM camp with NO explicit price pays nothing per window:
        // HnmStandardCampFinalizer deliberately ignores DkpPerHour and pays only the
        // open/close/claim/kill bonuses at End Camp. Reporting DkpPerHour here told officers
        // "+1 DKP awarded" on every window post for a payout that never happens. Claim/Kill-style
        // non-HNM windowed events DO pay windowsAttended × DkpPerHour via EndEventCoreAsync, so
        // they keep reporting it.
        var paysPerWindow = !(string.Equals(eventEntity.EventType, "HNM", StringComparison.OrdinalIgnoreCase)
                              && !HnmCampRules.IsWd(eventEntity));
        double? perWindowDkp = null;
        // The same window before any close bonus: what the addon's per-window price box shows.
        double? baseWindowDkp = null;
        if (attendanceWindow is not null)
        {
            if (HnmCampPricing.HonoursWindowAmount(eventEntity))
            {
                // A Standard HNM camp pays this window by its ROLE -- the open bonus on window 1,
                // the regular rate elsewhere, plus the close bonus on the window that closes the
                // camp out -- so resolve it through the same pricing the finalizer will use.
                //
                // It used to report DkpPerHour, which such a camp does not pay, so the expression
                // fell through to null. That is what left the addon's Open tab showing the regular
                // window rate: with no figure for the window itself, it fell back to the camp-level
                // quote for the next post.
                var campWindows = await _dbContext.EventAttendanceWindows
                    .AsNoTracking()
                    .Where(w => w.EventId == eventEntity.Id)
                    .ToListAsync(cancellationToken);
                var campLinkshell = await _dbContext.Linkshells
                    .AsNoTracking()
                    .FirstOrDefaultAsync(l => l.Id == eventEntity.LinkshellId, cancellationToken);
                perWindowDkp = HnmCampPricing.WindowValueFor(
                    eventEntity, campLinkshell, attendanceWindow.SequenceNumber,
                    HnmStandardCampFinalizer.ResolveCloseWindow(
                        campWindows, HnmCampRules.PostsOpenAndClose(eventEntity)),
                    attendanceWindow.DkpAmount, attendanceWindow.IsKillWindow,
                    HnmStandardCampFinalizer.HasMarkedCloseWindow(campWindows));
                baseWindowDkp = HnmCampPricing.BaseWindowValueFor(
                    eventEntity, campLinkshell, attendanceWindow.SequenceNumber,
                    attendanceWindow.DkpAmount, attendanceWindow.IsKillWindow);
            }
            else
            {
                perWindowDkp = attendanceWindow.DkpAmount
                    ?? (paysPerWindow ? eventEntity.DkpPerHour : null);
                baseWindowDkp = perWindowDkp;
            }
        }

        var matched = 0;
        var alreadyVerified = 0;
        var unmatched = new List<string>();
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

        // Every candidate name, by the shared CharacterResolver rule: a name that is one member's
        // roster name and another's alt belongs to the roster name (rare but possible if two
        // players share an alt name), and the Claim Shield and the window payout agree.
        var membershipByName = CharacterResolver.ForMemberships(
            membershipsWithUser.Select(pair => (pair.Membership, pair.User)));
        // Each member's MAIN, so a scan that matched through one of the alt names above can say so
        // ("Athmilk (alt of Edicius)") instead of silently renaming the alt to the main on the way
        // into the window row. Same precedence the name lookup uses: the membership's own character
        // name is the roster main, with the account main as the fallback for a membership that
        // never had one filled in.
        var mainNameByAppUserId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in membershipsWithUser)
        {
            var mainName = pair.Membership.CharacterName ?? pair.User.CharacterName;
            if (!string.IsNullOrWhiteSpace(mainName) && pair.Membership.AppUserId is { } mainAppUserId)
            {
                mainNameByAppUserId[mainAppUserId] = mainName.Trim();
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
                membershipByName.AddIfMissing(selfName, issuerMembership, CharacterNameSlot.Membership);

                // ...and treat it as their main for the alt readout, unless they already had one.
                // This is the name the roster has nothing else to offer for them, so calling any
                // later scan of it an "alt of" nothing would be the only other option.
                if (!string.IsNullOrWhiteSpace(issuerId) && !mainNameByAppUserId.ContainsKey(issuerId))
                {
                    mainNameByAppUserId[issuerId] = selfName;
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
            var membership = membershipByName.Resolve(name);
            if (membership is null)
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

        // Pre-load who is already credited for this window so the "already attended this window"
        // check is an in-memory lookup.
        //
        // Keyed on the DENORMALIZED AppUserId, not the participation id. A roster clear (the wyrm
        // camps clear every window) deletes a member's AppUserEvent and the next scan creates a
        // fresh one — so keying on the participation would let the same person be credited twice
        // for one window under two different participation rows. It also means dedupe still works
        // against rows whose participation has since been cleared away.
        var existingWindowAppUserIds = attendanceWindow is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await _dbContext.AppUserEventWindows
                .Where(w => w.EventAttendanceWindowId == attendanceWindow.Id && w.AppUserId != null)
                .Select(w => w.AppUserId!)
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

        foreach (var (entry, membership, scannedName) in resolvedEntries)
        {
            // Which character the scan actually saw, versus who the roster calls them. These differ
            // whenever a player shows up on an alt — membershipByName resolves alt names too — and
            // the window row below records BOTH: the alt is who was at camp, the main is who the
            // DKP belongs to. Null main means the roster has no other name for them, which is not
            // an alt sighting no matter what was scanned.
            var mainName = mainNameByAppUserId.GetValueOrDefault(membership.AppUserId!);
            var scannedIsAlt = !string.IsNullOrWhiteSpace(mainName)
                && !string.Equals(mainName, scannedName, StringComparison.OrdinalIgnoreCase);

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
                if (!existingWindowAppUserIds.Add(membership.AppUserId!))
                {
                    // Add returns false when already present — covers both a prior post of this
                    // window and a duplicate name inside this same batch.
                    alreadyVerified++;
                    continue;
                }

                _dbContext.AppUserEventWindows.Add(new AppUserEventWindow
                {
                    AppUserEvent = participation,
                    EventAttendanceWindow = attendanceWindow,
                    // Denormalized so the snapshot survives a roster clear deleting the
                    // participation above — see AppUserEventWindow.AppUserEventId.
                    AppUserId = membership.AppUserId,
                    // The SCANNED name, not the participation's. A player on an alt is credited
                    // through their account either way, but the window is a record of who was
                    // standing there — rewriting the alt to the main here is what made the web and
                    // Activity rosters disagree with the addon's own "(alt of X)" readout.
                    CharacterName = scannedName,
                    MainCharacterName = scannedIsAlt ? mainName : null,
                    VerifiedAt = nowUtc,
                    VerifiedBy = verifiedBy,
                    AllianceNumber = postedAllianceNumber,
                    AllianceKey = postedAllianceKey,
                    Zone = string.IsNullOrWhiteSpace(entry.Zone) ? null : entry.Zone.Trim()
                });

                creditedAttendees.Add(new
                {
                    // Echoes the scanned character for the same reason: the addon prints this list
                    // straight back under the post, next to the names it just scanned.
                    characterName = scannedName,
                    mainCharacterName = scannedIsAlt ? mainName : null,
                    jobName       = participation.JobName,
                    subJobName    = participation.SubJobName,
                    dkpEarned     = perWindowDkp
                });
            }

            // No "Verify" status-ledger row any more: nothing ever read it. On a windowed camp the
            // AppUserEventWindow row above is the record of who was scanned in which window.
            matched++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            matched,
            alreadyVerified,
            unmatched,
            windowSequence = attendanceWindow?.SequenceNumber,
            windowId = attendanceWindow?.Id,
            // Normalized, not raw — a window written before the Open/Close rename would otherwise
            // report its old "On Time" / "Claim/Kill" name back to the addon.
            windowLabel = HnmConfig.NormalizeWindowLabel(attendanceWindow?.Label),
            dkpPerWindow = perWindowDkp,
            baseDkpPerWindow = baseWindowDkp,
            // Echoed so the addon can colour its local copy of this window the same way a re-read
            // from the server would. Null when the post carried no alliance -- the addon then
            // renders those rows muted rather than assuming they are the reader's own.
            allianceNumber = postedAllianceNumber,
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
        // Matched against the ROW's own name first, then the participation's. The addon sends back
        // whatever name it is showing, and that is the row's: the scanned character, which for a
        // player on an alt is not the name on their participation. Matching only through the
        // navigation also meant a row whose participation had been cleared away (the wyrm camps
        // clear every window) could never be removed at all.
        var attendee = attendanceWindow.Attendees.FirstOrDefault(a =>
            string.Equals(a.CharacterName, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? attendanceWindow.Attendees.FirstOrDefault(a =>
                a.AppUserEvent != null
                && string.Equals(a.AppUserEvent.CharacterName, trimmed, StringComparison.OrdinalIgnoreCase));
        if (attendee is null) return NotFound(new { error = "Attendee not found in this window." });

        await RemoveWindowAttendeeRowAsync(attendee, cancellationToken);
        return Ok(new { removedId = attendee.Id });
    }

    // UNDO A WHOLE WINDOW POST. The attendee-at-a-time removal above fixes a roster; this is for
    // the post that should never have happened -- the wrong camp, the wrong window, the Post
    // pressed twice while the officer was still gathering.
    //
    // Deletes the window and everything the post wrote for it (the per-attendee rows), exactly as
    // RemoveWindowAttendeeRowAsync does one at a time, so a re-post lands as cleanly as a first post.
    //
    // WHAT IT DELIBERATELY LEAVES. The participations (AppUserEvent). A post CREATES one for
    // anyone who had none, but it does not own them -- a member who signed up on the Discord board
    // has had one since before the camp started, and there is no way to tell the two apart after
    // the fact. They are also harmless: on a windowed camp every payout is computed from the
    // windows a member was scanned in, so a participation with no windows left earns nothing.
    // Removing them would be the one irreversible part of an "undo".
    //
    // The camp's start time stays too. Auto-commence happened because an officer posted at a camp
    // that was running; that it was the wrong window does not mean the camp never started.
    [HttpDelete("events/{eventId:int}/windows/{sequence:int}")]
    [AddonApiAuth]
    public async Task<IActionResult> UndoWindowPostAsync(
        int eventId, int sequence, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var attendanceWindow = await _dbContext.EventAttendanceWindows
            .Include(w => w.Event)
            .Include(w => w.Attendees)
            .FirstOrDefaultAsync(
                w => w.EventId == eventId && w.SequenceNumber == sequence,
                cancellationToken);

        if (attendanceWindow is null) return NotFound(new { error = "That window has no post to undo." });
        if (attendanceWindow.Event is null || attendanceWindow.Event.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }
        // Same bar as posting one: undoing a post is the same authority as making it.
        if (!await TokenIssuerCanModerateAsync(token, attendanceWindow.Event.LinkshellId, cancellationToken))
        {
            return Forbid();
        }

        return await DeleteAttendanceWindowAsync(attendanceWindow, cancellationToken);
    }

    // The shared body of both "delete this window" endpoints: the addon's undo above, and the
    // Activity's x on a live camp's window panel (DeleteWindowManagementAsync). The caller has
    // loaded the window with its Event and Attendees and checked who is asking; this applies the
    // two guards and removes the window with everything its post wrote.
    private async Task<IActionResult> DeleteAttendanceWindowAsync(
        EventAttendanceWindow attendanceWindow, CancellationToken cancellationToken)
    {
        var eventId = attendanceWindow.EventId;
        var sequence = attendanceWindow.SequenceNumber;

        // An ENDED camp has already handed its windows to a review card (HnmCampReviewHandoffService)
        // and its DKP is computed from that copy. Deleting the live row would change nothing an
        // officer can see and everything they cannot, so the correction belongs on the review card.
        if (attendanceWindow.Event?.EndTime is not null)
        {
            return BadRequest(new
            {
                error = "This camp has ended. Fix its roster on the review card under "
                      + "Events Pending DKP Post instead.",
            });
        }

        // UNWIND IN ORDER ON AN OPEN/CLOSE CAMP. Posting is one-way there: once a Close exists the
        // post endpoint refuses to create any further window, so undoing the Open underneath one
        // would leave a camp that cannot be repaired from the addon at all -- no Open, and no way
        // to post a new one. Undo the Close first and both come back.
        //
        // Only on a 2-post camp, which is the only shape that guard applies to; a numbered camp
        // re-posts any window freely, so it may be undone in any order.
        if (attendanceWindow.Event is not null
            && HnmCampRules.PostsOpenAndClose(attendanceWindow.Event)
            && !attendanceWindow.IsKillWindow
            && await _dbContext.EventAttendanceWindows.AnyAsync(
                w => w.EventId == eventId && w.SequenceNumber > sequence && !w.IsKillWindow,
                cancellationToken))
        {
            return BadRequest(new
            {
                error = "Undo the Close first — this camp will not accept a new Open while the "
                      + "Close is filed.",
            });
        }

        var removed = attendanceWindow.Attendees.Count;

        _dbContext.AppUserEventWindows.RemoveRange(attendanceWindow.Attendees);
        _dbContext.EventAttendanceWindows.Remove(attendanceWindow);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { undone = true, sequence, removedAttendees = removed });
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
                // Same FFXI-job-id-indexed int[] shape as the main's JobLevels below, so the
                // addon's "Can equip" test runs unchanged over an alt.
                Alt1JobLevels = link.AppUser != null ? link.AppUser.Alt1JobLevels : null,
                Alt2JobLevels = link.AppUser != null ? link.AppUser.Alt2JobLevels : null,
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

                // altDetails carries each alt's OWN job levels. `alts` (names only) stays as it
                // is: an addon build that predates this reads that key, and dropping it would
                // blank the alt handling those versions already do.
                var altDetails = new List<object>(2);
                if (!string.IsNullOrWhiteSpace(row.Alt1))
                {
                    altDetails.Add(new { name = row.Alt1!.Trim(), jobLevels = row.Alt1JobLevels });
                }
                if (!string.IsNullOrWhiteSpace(row.Alt2))
                {
                    altDetails.Add(new { name = row.Alt2!.Trim(), jobLevels = row.Alt2JobLevels });
                }

                return new
                {
                    characterName = row.CharacterName,
                    alts,
                    altDetails,
                    jobLevels = row.JobLevels
                };
            })
            .ToList();

        return Ok(new
        {
            characterNames,
            roster,
            lootStructure = LootStructures.NormalizeLootStructure(linkshell?.LootStructure ?? "Dkp")
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

    // Posts one loot row from the addon Loot Pool panel, addressed by EVENT.
    //
    // This is the route the addon uses now, and it is the one that matches what the officer is
    // doing: they have a camp selected and are handing out its drops. The ToD route below still
    // works, but it made loot depend on an observation ("Fafnir died at 21:04") that has nothing
    // to do with who killed it -- and because the server needs a target row either way, the addon
    // had to either auto-post a ToD nobody asked for or refuse the loot until one existed.
    [HttpPost("events/{eventId:int}/loot")]
    [AddonApiAuth]
    public async Task<IActionResult> PostEventLootAsync(
        int eventId,
        [FromBody] AddonPostLootRequest request,
        [FromServices] ManualLootService manualLoot,
        [FromServices] AddonLootReplayGuard lootReplay,
        CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var eventEntity = await _dbContext.Events
            .Include(e => e.Linkshell)
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (eventEntity is null)
        {
            return NotFound(new { error = "Event not found." });
        }
        if (eventEntity.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }

        // Always addressed as the event; ManualLootService decides where it really goes. A running
        // camp holds it until End Camp; a camp that has already ended (the board parked for the
        // next pop) files it under that camp's Past Event and charges it now; no camp to file it
        // under is a plain refusal. This used to test EndTime, which End Camp never sets on a
        // board, so loot posted right after End Camp waited, uncharged, for the NEXT camp.
        var target = new ManualLootTarget(ManualLootTargetKind.LiveEvent, eventEntity.Id, null);

        return await AddAddonLootAsync(
            token, eventEntity.Linkshell, request, target, manualLoot, lootReplay, cancellationToken);
    }

    // Posts one loot row addressed by ToD id. Kept for compatibility -- the addon posts by event
    // now -- and still files the row against the EVENT for that monster (see below) so an in-game
    // drop lands in the Loot History against a camp rather than as a bare "ToD".
    [HttpPost("tod/{todId:int}/loot")]
    [AddonApiAuth]
    public async Task<IActionResult> PostTodLootAsync(
        int todId,
        [FromBody] AddonPostLootRequest request,
        [FromServices] ManualLootService manualLoot,
        [FromServices] AddonLootReplayGuard lootReplay,
        CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var tod = await _dbContext.Tods
            .Include(t => t.Linkshell)
            .FirstOrDefaultAsync(t => t.Id == todId, cancellationToken);
        if (tod is null)
        {
            return NotFound(new { error = "Tod not found." });
        }
        if (tod.LinkshellId != token.LinkshellId)
        {
            return Forbid();
        }

        // Loot from the addon is filed against the EVENT for this monster, not against the ToD.
        //
        // A ToD is an observation ("Fafnir died at 21:04"); the loot belongs to the camp that
        // killed it. Filing it on the ToD is what put every in-game drop in the Loot History as
        // source "ToD" with no event behind it. Merge-pair tolerant, so a Fafnir kill finds a
        // "Fafnir/Nidhogg" camp.
        var monsterMatches = HnmConfig.MonsterMatchNamesLower(tod.MonsterName);

        // Matched on AssignedMonsterName FIRST, then on EventName.
        //
        // EventName alone was the whole test, and it only worked while officers happened to name
        // a camp after its monster. A camp called "faf" (or "Fafnir Tues", or anything else an
        // officer actually types) matched nothing, so an in-game drop fell through to the past-
        // event fallback and then to no event at all -- posted, DKP deducted, and invisible in the
        // live camp's Loot section. AssignedMonsterName is the camp's ACTUAL monster and is what
        // the ToD, the board and the tracker all key on.
        //
        // EventName is kept as a second chance rather than replaced: a camp created before the
        // monster was assignable, or one an officer left unassigned, still resolves the old way.
        async Task<int?> FindMonsterEventAsync(bool runningOnly)
        {
            var events = _dbContext.Events
                .AsNoTracking()
                .Where(evt => evt.LinkshellId == token.LinkshellId);
            if (runningOnly)
            {
                events = events.Where(EventRunState.Running);
            }

            var id = await events
                .Where(evt => evt.AssignedMonsterName != null
                              && monsterMatches.Contains(evt.AssignedMonsterName.ToLower()))
                .OrderByDescending(evt => evt.CommencementStartTime ?? evt.StartTime)
                .Select(evt => (int?)evt.Id)
                .FirstOrDefaultAsync(cancellationToken);
            id ??= await events
                .Where(evt => evt.EventName != null
                              && monsterMatches.Contains(evt.EventName.ToLower()))
                .OrderByDescending(evt => evt.CommencementStartTime ?? evt.StartTime)
                .Select(evt => (int?)evt.Id)
                .FirstOrDefaultAsync(cancellationToken);
            return id;
        }

        // A RUNNING camp first (EventRunState). Any matching board used to count as live, so the
        // board End Camp had just parked caught the drop and held it, uncharged, for the next camp.
        var liveEventId = await FindMonsterEventAsync(runningOnly: true);

        // No running camp: the kill's camp has most likely just ended. Its board's newest archive
        // is that camp's own Past Event, so the drop goes there and is charged now.
        int? pastEventId = null;
        if (liveEventId is null && await FindMonsterEventAsync(runningOnly: false) is int boardId)
        {
            pastEventId = await EventRunState.NewestCampArchiveIdAsync(_dbContext, boardId, cancellationToken);
        }

        // Still nothing: fall back to the most recent past event by name before giving up and
        // filing it with no event at all.
        if (liveEventId is null && pastEventId is null)
        {
            // EventHistory carries no AssignedMonsterName -- it stores the camp's NAME at close --
            // so this stays a name match. A camp named for something other than its monster is
            // therefore still unreachable here, which is exactly why the live lookup above no
            // longer depends on the name.
            pastEventId = await _dbContext.EventHistories
                .AsNoTracking()
                .Where(history => history.LinkshellId == token.LinkshellId
                                  && history.EventName != null
                                  && monsterMatches.Contains(history.EventName.ToLower()))
                .OrderByDescending(history => history.EndTime ?? history.StartTime)
                .Select(history => (int?)history.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var target = liveEventId is int live
            ? new ManualLootTarget(ManualLootTargetKind.LiveEvent, live, null)
            : pastEventId is int past
                ? new ManualLootTarget(ManualLootTargetKind.PastEvent, null, past)
                : ManualLootTarget.None;

        return await AddAddonLootAsync(
            token, tod.Linkshell, request, target, manualLoot, lootReplay, cancellationToken);
    }

    // The shared half of both addon loot routes: validate the request, check the winner can
    // afford it, and write the row. Only the TARGET differs between the two -- an event id the
    // addon already knows, or one resolved from a ToD's monster -- and the caller supplies it.
    //
    // Extracted rather than duplicated because every rule below is a payout rule. Two copies of
    // "can this member afford it" is two chances to disagree about someone's balance.
    private async Task<IActionResult> AddAddonLootAsync(
        AddonApiToken token,
        Linkshell? linkshell,
        AddonPostLootRequest request,
        ManualLootTarget target,
        ManualLootService manualLoot,
        AddonLootReplayGuard lootReplay,
        CancellationToken cancellationToken)
    {
        // Posting loot takes DKP off the winner, so it needs a loot right -- the same one the app's
        // Add loot asks for (CanAddLoot), or the addon's officer right. It used to need only a
        // paired token, so any member (even a Trial, or someone since removed) could name anyone
        // as the winner and take their DKP from the addon's Loot Pool panel.
        if (!await TokenIssuerCanAsync(token, token.LinkshellId, PostsAddonLoot, cancellationToken))
        {
            return new ObjectResult(new
            {
                error = "You don't have permission to post loot. Ask a linkshell officer to give your role Add loot."
            })
            { StatusCode = StatusCodes.Status403Forbidden };
        }

        // A retry of a drop whose first post already went through (the addon gave up waiting for
        // the answer) gets that first answer back, and nobody is charged twice. Checked AFTER the
        // permission gate, so a token that has lost the loot right gets no replay either.
        var outcome = await lootReplay.RunOnceAsync(
            token.Id,
            request.ClientRequestId,
            async () =>
            {
                var written = await WriteAddonLootAsync(
                    token, linkshell, request, target, manualLoot, cancellationToken);
                return (written, written is OkObjectResult);
            },
            cancellationToken);

        // A replay answers with a fresh result around the first post's body.
        return outcome.Replayed && outcome.Response is OkObjectResult first
            ? Ok(first.Value)
            : outcome.Response;
    }

    // Validates and writes one addon loot row. Only AddAddonLootAsync calls it, after the
    // permission gate and inside the replay guard.
    private async Task<IActionResult> WriteAddonLootAsync(
        AddonApiToken token,
        Linkshell? linkshell,
        AddonPostLootRequest request,
        ManualLootTarget target,
        ManualLootService manualLoot,
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

        var lootStructure = LootStructures.NormalizeLootStructure(linkshell?.LootStructure ?? "Dkp");
        if (lootStructure == "LootCouncil")
        {
            return BadRequest(new { error = "Linkshell uses LootCouncil - DKP loot posts are disabled." });
        }
        if (lootStructure == "Hybrid" && request.WinningDkpSpent.Value > 100)
        {
            return BadRequest(new { error = "Deduction % cannot exceed 100." });
        }

        // Winner must be on the linkshell's roster (case-insensitive on CharacterName) -- the same
        // guard CreateTodAsync uses.
        var winnerMembership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(link => link.LinkshellId == token.LinkshellId
                        && link.CharacterName != null
                        && link.CharacterName.ToLower() == itemWinner.ToLower(),
                cancellationToken);
        if (winnerMembership?.CharacterName is null)
        {
            return BadRequest(new { error = "Winner must be a current linkshell member." });
        }
        var rosterMatch = winnerMembership.CharacterName;

        // DKP-structure linkshells: a win cannot exceed the winner's AVAILABLE DKP (total minus
        // whatever is locked by active auction bids). Hybrid spends a % of the current balance so
        // it cannot overrun; LootCouncil already returned above.
        if (lootStructure == "Dkp" && winnerMembership.AppUserId is { } winnerUserId)
        {
            // Paid from whichever pool "HNM" maps to -- the same default the ToD loot form uses --
            // so the affordability check has to read that wallet, not the total.
            var poolMap = await _dkpPools.GetMapAsync(token.LinkshellId, cancellationToken);
            var poolId = poolMap.Resolve("HNM");
            var availableDkp = await AuctionDkpService.ComputePoolAvailableDkpAsync(
                _dbContext, _dkpPoolBalances, winnerUserId, token.LinkshellId, poolId, cancellationToken);
            if (request.WinningDkpSpent.Value > availableDkp)
            {
                // No number: this goes back to whoever posted, and a member's balance is theirs.
                var poolLabel = poolMap.HasMultiplePools ? $" {poolMap.NameFor(poolId)}" : string.Empty;
                return BadRequest(new { error = $"{rosterMatch} doesn't have enough available{poolLabel} DKP for that." });
            }
        }

        var result = await manualLoot.AddAsync(
            token.LinkshellId,
            target,
            itemName,
            rosterMatch,
            request.WinningDkpSpent.Value,
            dkpPoolId: null,
            recordedByAppUserId: token.IssuedToAppUserId,
            cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error ?? "Adding loot failed." });
        }

        return Ok(new
        {
            lootDetailId      = result.Detail!.Id,
            itemName          = result.Detail.ItemName,
            itemWinner        = result.Detail.ItemWinner,
            winningDkpSpent   = result.Detail.WinningDkpSpent,
            actualDeductedDkp = result.Detail.ActualDeductedDkp,
            // Echoed so the addon can say which camp it landed on, or that it landed on none.
            eventLinked       = target.Kind != ManualLootTargetKind.None
        });
    }

    // Addon-side ToD posting. Mirrors the Discord Activity create-tod path in
    // ActivityWindowEventsController but with addon-token auth (linkshell-scoped) and
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

        // The LINKSHELL'S configured values, not the global defaults. An addon-posted ToD used to
        // ignore the configuration entirely, because it only ever reached the Activity's client.
        var cooldown = await TodCooldowns.GetDefaultTodCooldownAsync(
            _monsterTimings, token.LinkshellId, monsterName, cancellationToken);
        var interval = await TodCooldowns.GetDefaultTodIntervalAsync(
            _monsterTimings, token.LinkshellId, monsterName, cancellationToken);
        // The monster's Additional Seconds too, the same as the Activity's Log ToD and both End Camp
        // paths. Leaving them off made a ToD posted in game start its reposted board early, by
        // exactly that many seconds, and every window on it with them.
        var timing = await _monsterTimings.ResolveAsync(token.LinkshellId, monsterName, cancellationToken);
        var additionalSeconds = Math.Max(0, timing.AdditionalSeconds);

        // Claim arrives verbatim on the request — tri-state:
        //   true  -> "Post (Claimed)" pressed in the ToD Capturing panel
        //   false -> "Post (Unclaimed)"
        //   null  -> ToD was auto-posted from the loot-pool flow before the
        //            user picked; the addon UI keeps the buttons live so they
        //            can settle it via an update call afterward.
        // No server-side guesswork — chat-line parsing and active-event
        // heuristics produced too many false negatives on real linkshells.
        var claimed = request.Claim;

        // Manage ToDs saves it straight to the list; Submit ToD for approval queues it for an
        // officer instead (below). Neither -> refused.
        var canManage = await TokenIssuerCanAsync(token, token.LinkshellId, r => r.CanManageTods, cancellationToken);
        if (!canManage
            && !await TokenIssuerCanAsync(token, token.LinkshellId, r => r.CanSubmitTodForApproval, cancellationToken))
        {
            return Forbid();
        }

        // Normalize the optional day cycle number. 0 / negative -> null so the
        // auto-event helpers don't render "Nidhogg D0".
        var dayNumber = request.DayNumber.HasValue && request.DayNumber.Value > 0
            ? request.DayNumber
            : null;

        // Only a positive window is a reading; 0 / negative is the addon saying it has none, and
        // must stay null so "Popped on window" renders as "not recorded" rather than as window 0.
        var popWindow = request.PopWindow is { } pw && pw > 0 ? pw : (int?)null;

        if (canManage)
        {
            // Saved the one shared way. When another character already posted this kill, this post
            // is the same ToD, not a second one: its claim and window are added to that row and
            // nothing else runs (TodRecorder).
            var recorder = HttpContext.RequestServices.GetRequiredService<TodRecorder>();
            var recorded = await recorder.RecordAsync(
                new TodRecordInput(
                    token.LinkshellId,
                    monsterName,
                    Hq: null, // the name says it: the addon posts the half that died
                    defeatedAtUtc,
                    additionalSeconds,
                    popWindow,
                    claimed,
                    Killed: null,
                    dayNumber,
                    TodSources.Addon,
                    token.IssuedToAppUserId)
                {
                    Cooldown = cooldown,
                    Interval = interval,
                    DayFromLiveCamp = true,
                },
                cancellationToken);

            // Recording a Time of Death does NOT put a board up. It used to queue the next pop's camp
            // on the spot, for any tracked HNM, which meant entering a ToD created an event nobody had
            // asked for -- and, when the camp it belonged to was still live, a second one beside it.
            // A board comes back only through Repeat-on-ToD, at the lead the create form set
            // (HnmRecurringBoardBackgroundService).
            return Ok(TodPostResponse(recorded.Tod, alreadyPosted: recorded.MergedIntoExisting));
        }

        // A member whose kill another character already posted has nothing left to queue for
        // approval, and may not change the saved row.
        var sameKill = await TodSameKill.FindAsync(
            _dbContext, token.LinkshellId, monsterName, defeatedAtUtc, cancellationToken);
        if (sameKill is not null)
        {
            return Ok(TodPostResponse(sameKill, alreadyPosted: true));
        }

        // A live camp for this spawn decides the day -- see HnmDayCycle.DayForAddonTodAsync. (A
        // saved ToD gets this from TodRecorder.)
        dayNumber = await HnmDayCycle.DayForAddonTodAsync(
            _dbContext, token.LinkshellId, monsterName, dayNumber, cancellationToken);

        var approvalSvc = HttpContext.RequestServices.GetRequiredService<SubmissionApprovalService>();
        var input = new TodSubmissionInput(
            monsterName,
            dayNumber,
            claimed,
            defeatedAtUtc,
            cooldown,
            interval,
            TodRecorder.RepopFor(defeatedAtUtc, cooldown, additionalSeconds),
            null,
            // Carried into the queue, not dropped: the tracker stamped this when the mob
            // popped, and it is no less true for the submitter lacking ToD-manage rights.
            popWindow,
            // The addon posts the half that died ("Nidhogg"), so the name says whether it was HQ.
            HnmConfig.ResolveClaimHalf(monsterName, false).IsHq,
            additionalSeconds);
        var submissionId = await approvalSvc.QueueTodAsync(token.LinkshellId, token.IssuedToAppUserId!, input, cancellationToken);
        return Ok(new { pending = true, submissionId, monsterName });
    }

    private static object TodPostResponse(Tod tod, bool alreadyPosted) => new
    {
        todId = tod.Id,
        monsterName = tod.MonsterName,
        defeatedAtUtc = tod.Time,
        repopTimeUtc = tod.RepopTime,
        cooldown = tod.Cooldown,
        interval = tod.Interval,
        claim = tod.Claim,
        // So the addon can say why the repop it prints carries these seconds.
        additionalSeconds = tod.AdditionalSeconds,
        // So the addon can say another character got there first, and show the saved claim.
        alreadyPosted
    };

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

        // Changing a saved ToD's claim is editing that ToD, so it needs Manage ToDs - the same
        // check as posting one straight to the saved list above. A member whose post only matched
        // an officer's ToD gets that row's id back, but must not be able to rewrite it.
        if (!await TokenIssuerCanAsync(token, tod.LinkshellId, r => r.CanManageTods, cancellationToken))
        {
            return Forbid();
        }

        tod.Claim = request.Claim;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { todId = tod.Id, claim = tod.Claim });
    }
}
