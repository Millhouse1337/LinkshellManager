using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LinkshellManagerDiscordApp.Controllers;

// Adding a post to a live camp from the Activity's "Add New Post": a WINDOW post (members credited
// into one of the camp's attendance windows, the same rows the addon's Post writes) or a MISC post
// (a capture filed on the camp as Misc, paid the Misc rate once per person). Both take the members
// by account, picked from the linkshell roster -- an officer naming who was there, so there is no
// scanned name to resolve.
//
// Also home to FindOrCreateAttendanceWindowAsync, which the addon's PostAttendanceAsync shares, so
// the range check and the "the Close has been posted" guard cannot differ between the two.
public sealed partial class AddonApiController
{
    // Finds the camp's window row for `windowSequence`, creating it when this is the first post to
    // it. Returns an error message (for a 400) instead of a row when the post is not allowed.
    private async Task<(EventAttendanceWindow? Window, string? Error)> FindOrCreateAttendanceWindowAsync(
        Event eventEntity, int windowSequence, bool isKillWindow, DateTime nowUtc, string postedBySource,
        CancellationToken cancellationToken)
    {
        // Same effective count the board, the Activity DTO and the addon's event list use.
        // The old expression (WindowCountOverride ?? GetWindowCount(EventName)) returned 2 for
        // kings/dragons, so an addon-created board that never stamped WindowCountOverride
        // rejected every post past window 2 with "out of range".
        //
        // Range check only. A snapshot is filed against the SPAWN window it was taken in —
        // window 1 is the pop, window 5 is fifty minutes later — so the ceiling is the spawn
        // count, or the camp's Window Method where one is stamped (MaxPostSequence). Whether the
        // Close ends the posting is the separate question below.
        //
        // The kill roster is allowed ONE sequence past that. It is filed after the last window
        // by definition, and on a camp whose spawn count is also its post count (a 2-window NM)
        // there is no in-range sequence left for it — the range check would reject the very
        // post the Post Kill button exists to make. Everything else still has to name a real
        // window.
        var postsOpenAndClose = DiscordEventMessageBuilder.PostsOpenAndClose(eventEntity);
        var ceiling = isKillWindow
            ? DiscordEventMessageBuilder.KillPostCeiling(eventEntity)
            : DiscordEventMessageBuilder.MaxPostSequence(eventEntity);
        if (windowSequence < 1 || windowSequence > ceiling)
        {
            return (null, $"Window sequence {windowSequence} is out of range for this event (max {ceiling}).");
        }

        var attendanceWindow = await _dbContext.EventAttendanceWindows
            .FirstOrDefaultAsync(
                w => w.EventId == eventEntity.Id && w.SequenceNumber == windowSequence,
                cancellationToken);

        if (attendanceWindow is null)
        {
            // On a 2-post camp the Close is the end of the camp, so it ends the posting.
            //
            // The addon stops offering the buttons, but that is not enough on its own: a
            // second officer's client only refreshes its event list, never its per-window
            // rosters (those rehydrate on event RE-SELECTION), so their launcher keeps
            // offering [Post Open] indefinitely after someone else posts the Close. Without
            // this, their click silently creates the Open the first officer deliberately
            // did not post — and hands everyone on it an open bonus.
            //
            // Deliberately scoped twice over:
            //   * to the CREATE path — a non-null attendanceWindow means this batch is
            //     landing in a window that already exists, which is the addon's per-window
            //     "add a member" backfill. That must keep working, including on the Close.
            //   * to a 2-POST camp — one that takes an Open and a Close. On a camp that posts
            //     per window a later window is routine and an earlier one is a legitimate
            //     correction. This reads the post count, not the spawn count: a king/dragon
            //     is a 2-post camp with 7 spawn windows, and testing it against the spawn
            //     count meant the guard never fired on exactly the camps it was written for.
            //   * and NOT to the Post Kill roster, which is filed after the Close by design.
            //     The kill read is the point of that button: people turn up for the fight who
            //     never sat the window, and on a 2-post camp there is no later sequence for
            //     them to land in. Excluding it here is what lets a king/dragon camp record a
            //     kill roster at all.
            if (postsOpenAndClose && !isKillWindow
                && await _dbContext.EventAttendanceWindows.AnyAsync(
                    w => w.EventId == eventEntity.Id && w.SequenceNumber == 2, cancellationToken))
            {
                return (null, "The Close window has been posted, so this camp accepts no further "
                              + "windows. To credit someone who was missed, add them to the Close window.");
            }

            attendanceWindow = await SaveNewWindowOrAdoptExistingAsync(_dbContext, new EventAttendanceWindow
            {
                EventId = eventEntity.Id,
                SequenceNumber = windowSequence,
                // A kill roster is named for what it is. Running it through the positional
                // labeller would call it "Window 3" or, worse, "Close" — the one name it must
                // never carry, since the close is the window an officer ticked.
                Label = isKillWindow
                    ? "Kill"
                    : DiscordEventMessageBuilder.DefaultWindowLabel(eventEntity, windowSequence),
                PostedAt = nowUtc,
                // Which spawn window this was taken in, kept apart from the sequence above.
                // Nothing can work it out later: the counter keeps ticking through the fight.
                SpawnWindow = DiscordEventMessageBuilder.SpawnWindowNow(eventEntity),
                PostedBySource = postedBySource,
                IsKillWindow = isKillWindow
            }, cancellationToken);

            // On a named Open/Close camp, posting the Close IS the officer saying "this closes the
            // camp", so tick it. The payout already treats the latest post as the close here
            // (ResolveCloseWindow); the tick makes the app's Closing window box agree with it.
            if (postsOpenAndClose && !isKillWindow && windowSequence == 2
                && !attendanceWindow.IsClosingWindow)
            {
                var siblings = await _dbContext.EventAttendanceWindows
                    .Where(w => w.EventId == eventEntity.Id)
                    .ToListAsync(cancellationToken);
                foreach (var sibling in siblings)
                {
                    // One close per camp, the same rule SetClosingWindowAsync enforces.
                    sibling.IsClosingWindow = sibling.Id == attendanceWindow.Id;
                }
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        if (isKillWindow && !attendanceWindow.IsKillWindow)
        {
            // Post Kill landing in a row that already exists — a second kill post correcting
            // the first, a retry, or a race lost to another officer's ordinary post. Promote it
            // rather than silently filing the kill roster as an ordinary window, which would pay
            // it the window rate and pay nobody the kill.
            //
            // Never demotes: an ordinary post into a kill row must not strip the flag, or the
            // per-window "add a member" backfill would turn the kill roster back into a window.
            attendanceWindow.IsKillWindow = true;
            attendanceWindow.IsClosingWindow = false;   // a kill roster can never be the close
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return (attendanceWindow, null);
    }

    // Saves a brand-new window row, or hands back the one another post created first.
    //
    // Two officers pressing Post on the same not-yet-posted window at the same moment both find no
    // row and both try to create it. The unique (EventId, SequenceNumber) index lets one through and
    // rejects the other, which used to surface as a bare 500 on a post that had done nothing wrong.
    // The loser now drops its own row and joins the winner's, exactly as if it had arrived a second
    // later -- its members are added to that window and anyone already in it is skipped as usual.
    //
    // Any other save failure, or a rejection with no winning row to be found, is rethrown untouched.
    internal static async Task<EventAttendanceWindow> SaveNewWindowOrAdoptExistingAsync(
        ApplicationDbContext dbContext, EventAttendanceWindow newWindow, CancellationToken cancellationToken)
    {
        dbContext.EventAttendanceWindows.Add(newWindow);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return newWindow;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Stop tracking ours, or the next SaveChanges on this request would try the insert again.
            // Anything else the failed save carried (the addon's auto-commence) stays pending and
            // goes out with that next save.
            dbContext.Entry(newWindow).State = EntityState.Detached;

            var winner = await dbContext.EventAttendanceWindows.FirstOrDefaultAsync(
                w => w.EventId == newWindow.EventId && w.SequenceNumber == newWindow.SequenceNumber,
                cancellationToken);
            if (winner is null) throw;
            return winner;
        }
    }

    // Runs a window post, re-running it if it lost a race on a person (see UniqueRaceRetry).
    //
    // Two officers posting the same window at the same moment, both naming Bob, both read "Bob is
    // not in this window yet" before either has saved, and the unique indexes on AppUserEventWindows
    // reject the second insert. Everything a window post saves before its credits (the window row,
    // its price, the close tick) is safe to repeat.
    internal static Task<T> RetryIfWindowCreditRaceAsync<T>(
        ApplicationDbContext dbContext, Func<Task<T>> post) =>
        UniqueRaceRetry.RunAsync(dbContext, post, "AppUserEventWindows");

    // windowSequence: which of the camp's windows; a new one is created, an existing one is added to.
    public sealed record AddonAddWindowPostRequest(int WindowSequence, IReadOnlyList<string>? AppUserIds);
    // name: optional; "Misc post" when blank.
    // DkpAmount: what this post pays each member, replacing the linkshell's Misc post rate. Null
    // (or left out) = no price of its own, the rate applies.
    public sealed record AddonAddMiscPostRequest(string? Name, IReadOnlyList<string>? AppUserIds, double? DkpAmount = null);

    // A live, per-window-priced camp, the officer posting to it, and the members they picked.
    private sealed record NewPostContext(
        Event Camp, AppUser Actor, string ActorName, IReadOnlyList<(string AppUserId, string CharacterName)> Members);

    private async Task<(NewPostContext? Context, IActionResult? Error)> LoadNewPostContextAsync(
        int eventId, IReadOnlyList<string>? appUserIds, CancellationToken cancellationToken)
    {
        var appUser = await ResolveManagementUserAsync(cancellationToken);
        if (appUser is null) return (null, Unauthorized(new { error = "Not signed in. Open /Identity/Account/Login on this same host first, or launch the activity inside Discord." }));

        var camp = await _dbContext.Events.FirstOrDefaultAsync(evt => evt.Id == eventId, cancellationToken);
        if (camp is null) return (null, NotFound(new { error = "Event not found." }));

        var actorMembership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(m => m.AppUserId == appUser.Id && m.LinkshellId == camp.LinkshellId, cancellationToken);
        if (!CanManageLinkshell(actorMembership)) return (null, Forbid());

        // Window and Misc posts PAY only on a Standard HNM camp; a Manual Check In camp credits its
        // check-in range instead.
        if (!HnmCampPricing.HonoursWindowAmount(camp))
        {
            return (null, BadRequest(new { error = "Only HNM camps that pay per window take posts." }));
        }
        if (camp.CommencementStartTime is null)
        {
            return (null, BadRequest(new { error = "This camp hasn't started yet." }));
        }
        if (camp.EndTime is not null)
        {
            return (null, BadRequest(new { error = "This camp has ended. Edit its review card instead." }));
        }

        var ids = (appUserIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return (null, BadRequest(new { error = "Pick at least one member for the post." }));
        }

        var memberships = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == camp.LinkshellId && m.AppUserId != null && ids.Contains(m.AppUserId))
            .ToListAsync(cancellationToken);
        if (memberships.Select(m => m.AppUserId).Distinct().Count() != ids.Count)
        {
            return (null, BadRequest(new { error = "Some of those members aren't in this linkshell." }));
        }

        var members = memberships
            .GroupBy(m => m.AppUserId!, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(m => (m.AppUserId!, (m.CharacterName ?? m.AppUser?.CharacterName ?? m.AppUser?.UserName ?? "Unknown member").Trim()))
            .ToList();
        var actorName = (actorMembership?.CharacterName ?? appUser.CharacterName ?? appUser.UserName ?? "officer").Trim();
        return (new NewPostContext(camp, appUser, actorName, members), null);
    }

    // POST /api/addon/management/events/{eventId}/window-posts   (web + Activity, cookie/bearer)
    //
    // Credits the picked members into window `windowSequence`, writing what the addon's Post writes
    // for each (PostAttendanceAsync): their participation, the per-window credit row, and a Verify
    // ledger entry. Someone already credited in that window is skipped, so posting into an existing
    // window is how an officer adds a missed member.
    [HttpPost("management/events/{eventId:int}/window-posts")]
    public Task<IActionResult> AddWindowPostManagementAsync(
        int eventId,
        [FromBody] AddonAddWindowPostRequest request,
        CancellationToken cancellationToken) =>
        RetryIfWindowCreditRaceAsync(
            _dbContext, () => AddWindowPostManagementCoreAsync(eventId, request, cancellationToken));

    private async Task<IActionResult> AddWindowPostManagementCoreAsync(
        int eventId, AddonAddWindowPostRequest request, CancellationToken cancellationToken)
    {
        var (context, error) = await LoadNewPostContextAsync(eventId, request.AppUserIds, cancellationToken);
        if (error is not null) return error;
        var (camp, actor, actorName, members) = context!;

        var nowUtc = DateTime.UtcNow;
        var source = $"{actorName} (app)";
        var (window, windowError) = await FindOrCreateAttendanceWindowAsync(
            camp, request.WindowSequence, isKillWindow: false, nowUtc, source, cancellationToken);
        if (windowError is not null) return BadRequest(new { error = windowError });

        var memberIds = members.Select(m => m.AppUserId).ToList();
        var participations = await _dbContext.AppUserEvents
            .Where(ue => ue.EventId == camp.Id && memberIds.Contains(ue.AppUserId!))
            .ToDictionaryAsync(ue => ue.AppUserId!, StringComparer.Ordinal, cancellationToken);
        // Keyed on the denormalized AppUserId, like the addon's Post: a roster clear can replace a
        // participation, and the credit row has to dedupe across that.
        var alreadyCredited = (await _dbContext.AppUserEventWindows
                .Where(w => w.EventAttendanceWindowId == window!.Id && w.AppUserId != null)
                .Select(w => w.AppUserId!)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var added = 0;
        var skipped = 0;
        foreach (var (appUserId, characterName) in members)
        {
            if (!alreadyCredited.Add(appUserId))
            {
                skipped++;
                continue;
            }

            if (!participations.TryGetValue(appUserId, out var participation))
            {
                participation = new AppUserEvent
                {
                    AppUserId = appUserId,
                    EventId = camp.Id,
                    CharacterName = characterName,
                    StartTime = nowUtc,
                    IsQuickJoin = true,
                    IsVerified = true,
                };
                _dbContext.AppUserEvents.Add(participation);
                participations[appUserId] = participation;
            }
            else if (participation.IsVerified != true)
            {
                participation.IsVerified = true;
                participation.StartTime ??= nowUtc;
            }

            _dbContext.AppUserEventWindows.Add(new AppUserEventWindow
            {
                AppUserEvent = participation,
                EventAttendanceWindow = window,
                AppUserId = appUserId,
                CharacterName = characterName,
                VerifiedAt = nowUtc,
                VerifiedBy = source,
            });
            _dbContext.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
            {
                AppUserEvent = participation,
                EventId = camp.Id,
                AppUserId = appUserId,
                ActionType = "Verify",
                OccurredAt = nowUtc,
                RequiresVerification = false,
                VerifiedAt = nowUtc,
                VerifiedBy = source,
                Source = "app",
                EventAttendanceWindow = window,
            });
            added++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new
        {
            windowId = window!.Id,
            windowSequence = window.SequenceNumber,
            windowLabel = HnmConfig.NormalizeWindowLabel(window.Label),
            added,
            alreadyInWindow = skipped,
        });
    }

    // POST /api/addon/management/events/{eventId}/misc-posts   (web + Activity, cookie/bearer)
    //
    // Files a new Misc post on the camp with the picked members: the same shape a `/lsm now` capture
    // takes once an officer files it as Misc (LinkedEventId + the Misc slot), already verified
    // because an officer made it. It pays the Misc rate once per person, or the DkpAmount the
    // officer typed into the form (same rules as editing it afterwards, ResolveMiscPostDkp).
    [HttpPost("management/events/{eventId:int}/misc-posts")]
    public async Task<IActionResult> AddMiscPostManagementAsync(
        int eventId,
        [FromBody] AddonAddMiscPostRequest request,
        CancellationToken cancellationToken)
    {
        var (context, error) = await LoadNewPostContextAsync(eventId, request.AppUserIds, cancellationToken);
        if (error is not null) return error;
        var (camp, actor, actorName, members) = context!;

        double? dkpAmount = null;
        if (request.DkpAmount is not null)
        {
            var linkshell = await _dbContext.Linkshells
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == camp.LinkshellId, cancellationToken);
            var (resolved, amountError) = ResolveMiscPostDkp(request.DkpAmount, linkshell);
            if (amountError is not null) return BadRequest(new { error = amountError });
            dkpAmount = resolved;
        }

        var nowUtc = DateTime.UtcNow;
        var snapshot = new AttendanceSnapshot
        {
            LinkshellId = camp.LinkshellId,
            CapturedAtUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            CapturedByCharacterName = actorName,
            Name = TruncateString(request.Name?.Trim(), 128) is { Length: > 0 } name ? name : "Misc post",
            PostedByAppUserId = actor.Id,
            SnapshotStatus = AttendanceSnapshotStatuses.Active,
            VerifiedAtUtc = nowUtc,
            VerifiedByAppUserId = actor.Id,
            LinkedEventId = camp.Id,
            EntryCount = members.Count,
            DkpAmount = dkpAmount,
        };
        WindowEventLinkService.ApplyCampSlot(
            snapshot,
            DiscordEventMessageBuilder.EffectiveWindowCount(camp),
            AttendanceSnapshotSlotKinds.Misc,
            windowNumber: null);
        foreach (var (appUserId, characterName) in members)
        {
            snapshot.Entries.Add(new AttendanceSnapshotEntry
            {
                Snapshot = snapshot,
                CharacterName = characterName,
                AppUserId = appUserId,
                AddedManually = true,
            });
        }
        _dbContext.AttendanceSnapshots.Add(snapshot);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { snapshotId = snapshot.Id, name = snapshot.Name, added = members.Count });
    }
}
