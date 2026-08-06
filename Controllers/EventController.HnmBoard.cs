using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public partial class EventController
{
    private static readonly HashSet<string> BoardTodCooldowns =
        new(TodManagerViewModel.SupportedCooldowns, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> BoardTodIntervals =
        new(TodManagerViewModel.SupportedIntervals, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> BoardLongWindowMonsters =
        new(StringComparer.OrdinalIgnoreCase) { "Tiamat", "Jormungand", "Vrtra" };

    // Logs (or edits) the monster's Time of Death from an HNM signup board's "Post ToD" /
    // "Edit ToD" button — the web mirror of ActivityDataController.PostBoardTodAsync. It
    // records the ToD (which drives the recurring-board re-post), moves the event's StartTime
    // to the predicted repop, wipes the board's signups, marks it "defeated / awaiting
    // re-post", and (via the DbContext save-hook on the modified Event) replaces the Discord
    // board message with a defeated note. The HnmRecurringBoardBackgroundService later clears
    // the flag and re-posts THIS same board LeadHours before the pop (one card that cycles).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PostBoardTod(
        int eventId,
        DateTime? todTimeLocal,
        string? cooldown,
        string? interval,
        int? dayNumber,
        bool? claim,
        // Did this linkshell get the kill? Drives the kill bonus at finalize in both attendance
        // modes. Null = unspecified, which defaults to killed (same as HnmCampPopService). This
        // form used to have no such field and hardcoded "killed", so ending a camp from the web
        // page always paid the kill bonus regardless of what actually happened.
        bool? killed,
        bool? hq,
        // The modal's "re-post the sign-up board before the next pop?" choice + its lead, the
        // web mirror of the Activity/Discord End Camp fields. Null repost = leave the monster's
        // standing Repeat-on-ToD config alone; null lead with repost on = keep its current lead.
        bool? repost,
        double? repostLeadHours,
        string? returnUrl)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventEntity = await _context.Events.FirstOrDefaultAsync(evt => evt.Id == eventId);
        if (eventEntity is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, eventEntity.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            TempData["PartySetupMessage"] = "Leader or officer access is required to log a Time of Death.";
            return SafeLocalRedirect(returnUrl);
        }

        var isHnm = string.Equals((eventEntity.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        if (!isHnm)
        {
            TempData["PartySetupMessage"] = "Time of Death can only be posted from an HNM signup board.";
            return SafeLocalRedirect(returnUrl);
        }

        var monsterName = eventEntity.AssignedMonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monsterName))
        {
            TempData["PartySetupMessage"] = "This HNM board has no monster assigned.";
            return SafeLocalRedirect(returnUrl);
        }

        // Blank = nobody saw it die (the window closed, or another linkshell took it): record no
        // ToD and no repop rather than inventing "now". Only a non-blank unparseable value errors.
        DateTime? todTimeUtc = null;
        if (todTimeLocal.HasValue)
        {
            todTimeUtc = ConvertUserTimeZoneToUtc(todTimeLocal, user.TimeZone);
            if (!todTimeUtc.HasValue)
            {
                TempData["PartySetupMessage"] = "Enter a valid Time of Death using your local time.";
                return SafeLocalRedirect(returnUrl);
            }
        }

        var resolvedCooldown = string.IsNullOrWhiteSpace(cooldown)
            ? GetDefaultBoardCooldown(monsterName)
            : cooldown.Trim();
        if (!BoardTodCooldowns.Contains(resolvedCooldown))
        {
            TempData["PartySetupMessage"] = "Select a valid cooldown.";
            return SafeLocalRedirect(returnUrl);
        }

        var resolvedInterval = interval?.Trim();
        if (string.IsNullOrWhiteSpace(resolvedInterval))
        {
            resolvedInterval = null;
        }
        else if (!BoardTodIntervals.Contains(resolvedInterval))
        {
            TempData["PartySetupMessage"] = "Select a valid interval.";
            return SafeLocalRedirect(returnUrl);
        }

        var nowUtc = DateTime.UtcNow;
        var repopUtc = todTimeUtc?.AddHours(ResolveBoardCooldownHours(resolvedCooldown));

        // Edit the existing ToD if we're already in the defeated/awaiting state (the card's
        // "Edit ToD" button); otherwise log a fresh ToD for this pop ("Post ToD").
        Tod? tod = null;
        if (eventEntity.HnmDefeatedAt != null && eventEntity.SourceTodId is { } sourceTodId)
        {
            tod = await _context.Tods.FirstOrDefaultAsync(t => t.Id == sourceTodId);
        }
        if (tod is null)
        {
            tod = new Tod { LinkshellId = eventEntity.LinkshellId, TotalTods = 1 };
            _context.Tods.Add(tod);
        }
        tod.MonsterName = monsterName;
        tod.DayNumber = dayNumber;
        // HQ (the merge pair's stronger monster) only exists from CombinedFromDay (day 4)
        // onward; force it off on earlier days so a stale value can't slip through the form.
        tod.Hq = (hq ?? false) && (dayNumber is null || dayNumber >= HnmConfig.CombinedFromDay);
        tod.Claim = claim;
        tod.Killed = killed;
        tod.Time = todTimeUtc;
        tod.Cooldown = resolvedCooldown;
        tod.RepopTime = repopUtc;
        tod.Interval = resolvedInterval;
        tod.TimeStamp = nowUtc;
        tod.TotalClaims = claim == true ? 1 : 0;
        await _context.SaveChangesAsync(); // ensure tod.Id is assigned

        // Re-point the event to this pop's repop time + ToD. With no ToD there's no predicted
        // repop, so the board keeps its StartTime rather than drifting to a guess.
        if (repopUtc is { } nextPopUtc)
        {
            eventEntity.StartTime = nextPopUtc;
        }
        eventEntity.SourceTodId = tod.Id;

        // Honor the modal's re-post choice first, so the lead read back below is the one the
        // officer just entered (same order as HnmCampPopService).
        if (repost is { } repostChoice)
        {
            await HnmRecurringBoardService.ApplyEndCampChoiceAsync(
                _context, eventEntity, monsterName, repostChoice, repostLeadHours, HttpContext.RequestAborted);
        }

        // The board auto-re-posts LeadHours before the pop — but only if Repeat-on-ToD is on
        // (an enabled recurring board exists). Otherwise there's no auto-re-post window.
        // Matched on every spelling of the spawn, the same way the poller finds the board.
        var monsterMatchNames = HnmConfig.MonsterMatchNamesLower(monsterName);
        var leadHours = await _context.HnmRecurringBoards
            .Where(b => b.LinkshellId == eventEntity.LinkshellId
                && monsterMatchNames.Contains(b.MonsterName.ToLower())
                && b.Enabled)
            .Select(b => (double?)b.LeadHours)
            .FirstOrDefaultAsync();
        // No repop to count back from = nothing to schedule, so the board won't auto-re-post.
        eventEntity.HnmRepostAt = repopUtc is { } repostAnchor && leadHours.HasValue
            ? repostAnchor.AddHours(-leadHours.Value)
            : null;

        // Null = the officer didn't say. Default to killed, matching HnmCampPopService's
        // `request.Killed ?? true` so both End Camp paths treat "unspecified" identically.
        var wasKilled = killed ?? true;

        var isWd = string.Equals(eventEntity.AttendanceMode, HnmAttendanceModes.Wd, StringComparison.OrdinalIgnoreCase);
        if (isWd)
        {
            eventEntity.WdClaimed = claim ?? false;
            eventEntity.WdKilled = wasKilled;
        }

        // BOTH modes hand the camp off to the Event System page's attendance sections as a pending review row instead of
        // paying DKP here; an officer's Post credits the ledger. Mirrors HnmCampPopService.PopAsync
        // — this is the web board's copy of the same End Camp transition.
        //
        // Must run BEFORE the wipe below: the Standard roster reads AppUserEventWindow, which
        // cascades off AppUserEventId. Staged into _context and committed by the SaveChanges below,
        // so the handoff and the teardown land together.
        //
        // popWindow is the window that was OPEN at the pop, not the awaited one the board
        // displays — ResolveCloseWindow matches it against posted snapshot sequences, which
        // are on the opened-window scale (window 1 = the camp's start / repop).
        await _campReviewHandoff.StageHandoffAsync(
            eventEntity,
            Math.Clamp(eventEntity.HnmWindowNumber, 1, DiscordEventMessageBuilder.EffectiveWindowCount(eventEntity)),
            claim ?? false,
            wasKilled,
            HttpContext.RequestAborted);

        eventEntity.HnmDefeatedAt = nowUtc;
        if (isWd)
        {
            // No grace left to wait out, so a Manual Check In board recycles here like Standard.
            // Every Manual Check In surface gates on WdFinalizedAt being null; HnmEventSeeder.ClearWdCampState
            // clears the block when the board is re-posted.
            eventEntity.WdFinalizedAt = nowUtc;
            eventEntity.HnmWindowNumber = 1;
        }

        // Wipe the board's signups — the pop is done. (Both the party-slot signups and the
        // no-slot attendees.) Deliberately do NOT stamp the recurring board's LastSourceTodId
        // here: leaving it lets the poller find this defeated event at the lead window and
        // re-post THIS same board instead of creating a new one.
        var slotSignups = await _context.EventPartySlotSignups
            .Where(s => s.EventId == eventId)
            .ToListAsync();
        _context.EventPartySlotSignups.RemoveRange(slotSignups);
        var noSlotAttendees = await _context.AppUserEvents
            .Where(p => p.EventId == eventId)
            .ToListAsync();
        _context.AppUserEvents.RemoveRange(noSlotAttendees);
        // Recycled board — clear this camp's attendance windows too, or the unique
        // (EventId, SequenceNumber) index hands the next camp's scans these stale rows.
        var scannedWindows = await _context.EventAttendanceWindows
            .Where(w => w.EventId == eventId)
            .ToListAsync();
        _context.EventAttendanceWindows.RemoveRange(scannedWindows);

        // The Event is now Modified (defeated note), so the DbContext save-hook enqueues it and
        // DiscordEventChannelPublisher edits the posted board message accordingly (single
        // renderer → no race with this save).
        await _context.SaveChangesAsync();

        TempData["PartySetupMessage"] = $"Logged {monsterName} Time of Death. The board re-posts before the next pop.";
        return SafeLocalRedirect(returnUrl);
    }

    // Window-cycle HNMs (Tiamat/Jormungand/Vrtra) default to the long 72-hour window; the
    // rest default to the standard 22-hour cooldown. Mirrors the ToDs tab default logic.
    private static string GetDefaultBoardCooldown(string? monsterName) =>
        !string.IsNullOrWhiteSpace(monsterName) && BoardLongWindowMonsters.Contains(monsterName.Trim())
            ? TodManagerViewModel.SeventyTwoHourCooldown
            : TodManagerViewModel.TwentyTwoHourCooldown;

    private static double ResolveBoardCooldownHours(string cooldown) => cooldown.Trim() switch
    {
        TodManagerViewModel.FiveMinuteCooldown => 5d / 60d,
        TodManagerViewModel.TwoHourCooldown => 2d,
        TodManagerViewModel.SeventyTwoHourCooldown => 72d,
        _ => 22d
    };
}
