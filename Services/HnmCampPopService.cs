using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// The single home for "pop / end an HNM camp": log the Time of Death, re-point the board to the
// next predicted repop, and stop the camp — shared by the Discord "🏁 Pop / End Camp" modal and the
// Activity Post-ToD / End-Camp form so the two behave identically. Modeled on WdCampFinalizer (the
// existing precedent for logic shared across Discord + Activity + web).
//
// BOTH modes now behave the same way: the board tears down and enters the defeated / auto-re-post
// cycle, and the camp's roster is handed to HnmCampReviewHandoffService as a pending review row on
// the Event System page's attendance sections. DKP is NOT paid here — an officer's Post credits it. (Manual Check In
// used to sit in "Awaiting Processing" for a configurable grace while a background service paid it;
// both are gone, replaced by that review step.) Callers own their own auth + input parsing; this
// service does the DB writes. The tracked-event SaveChanges lets the DbContext hook
// (CollectEditedEvents) edit the posted board message.
public sealed class HnmCampPopService
{
    private readonly ApplicationDbContext _db;
    private readonly HnmCampReviewHandoffService _handoff;
    private readonly ILogger<HnmCampPopService> _logger;

    public HnmCampPopService(
        ApplicationDbContext db, HnmCampReviewHandoffService handoff, ILogger<HnmCampPopService> logger)
    {
        _db = db;
        _handoff = handoff;
        _logger = logger;
    }

    public sealed record Request(
        int EventId,
        DateTime? TodTimeUtc,   // null => no ToD observed; Time + RepopTime are left unrecorded
        string? Cooldown,       // null/blank => monster's default cooldown
        string? Interval,
        int? DayNumber,
        bool? Claimed,
        bool? Killed,           // null => defaults to killed
        int? PopWindow,         // null => the camp's current window
        int AdditionalSeconds = 0,
        bool? Hq = null,
        // Re-post control (the End Camp form's "re-post the board before the next pop?" choice):
        //   null  => leave the monster's standing Repeat-on-ToD config untouched
        //   true  => enable it (upsert an HnmRecurringBoard from this event; RepostLeadHours = lead)
        //   false => disable it (the board won't auto-re-post this cycle)
        bool? Repost = null,
        double? RepostLeadHours = null,
        // Optional kill screenshot from the End Camp form (already sanitized to an
        // /uploads/tods/... path). null/blank => leave the ToD's existing image alone.
        string? ImagePath = null);

    public sealed record Result(bool Success, string? Error, DateTime? RepopTimeUtc, DateTime? RepostAtUtc);

    public async Task<Result> PopAsync(Request request, CancellationToken cancellationToken)
    {
        var ev = await _db.Events.FirstOrDefaultAsync(e => e.Id == request.EventId, cancellationToken);
        if (ev is null)
        {
            return new Result(false, "That event is no longer open.", null, null);
        }
        if (!string.Equals((ev.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase))
        {
            return new Result(false, "Only an HNM camp can be ended this way.", null, null);
        }
        var monster = ev.AssignedMonsterName?.Trim();
        if (string.IsNullOrWhiteSpace(monster))
        {
            return new Result(false, "This HNM board has no monster assigned.", null, null);
        }
        if (ev.WdFinalizedAt is not null)
        {
            return new Result(false, "This camp has already been processed.", null, null);
        }

        var nowUtc = DateTime.UtcNow;
        // No ToD supplied = nobody saw it die (the window closed, or another linkshell took it).
        // Both the time and the repop stay null rather than being invented from "now" — every
        // surface renders that as "Not entered". Only an actual observation writes a timestamp.
        var todTimeUtc = request.TodTimeUtc;
        var additionalSeconds = Math.Max(0, request.AdditionalSeconds);
        var cooldown = string.IsNullOrWhiteSpace(request.Cooldown)
            ? ActivityDataController.GetDefaultTodCooldown(monster)
            : request.Cooldown.Trim();
        var repopUtc = todTimeUtc
            ?.AddHours(ActivityDataController.ResolveTodCooldownHours(cooldown))
            .AddSeconds(additionalSeconds);

        var claimed = request.Claimed ?? false;
        var killed = request.Killed ?? true;

        // Edit the camp's existing ToD when re-logging after it already popped (either mode);
        // otherwise log a fresh ToD for this pop.
        Tod? tod = null;
        if (ev.SourceTodId is { } sourceTodId
            && (ev.HnmDefeatedAt is not null || ev.WdAwaitingProcessingSince is not null))
        {
            tod = await _db.Tods.FirstOrDefaultAsync(t => t.Id == sourceTodId, cancellationToken);
        }
        if (tod is null)
        {
            tod = new Tod { LinkshellId = ev.LinkshellId, TotalTods = 1 };
            _db.Tods.Add(tod);
        }
        tod.MonsterName = monster;
        tod.DayNumber = request.DayNumber ?? ev.DayNumber;
        tod.Claim = claimed;
        // Persist the kill outcome beside the claim. Both End Camp surfaces (the Activity form and
        // the Discord modal) already collect it and it already drives the kill bonus in both
        // modes — it just wasn't recorded anywhere queryable until now.
        tod.Killed = killed;
        tod.Hq = request.Hq ?? false;
        tod.AdditionalSeconds = additionalSeconds;
        tod.Time = todTimeUtc;
        tod.Cooldown = cooldown;
        tod.RepopTime = repopUtc;
        tod.Interval = string.IsNullOrWhiteSpace(request.Interval) ? null : request.Interval.Trim();
        tod.TimeStamp = nowUtc;
        tod.TotalClaims = claimed ? 1 : 0;
        if (!string.IsNullOrWhiteSpace(request.ImagePath))
        {
            tod.ImagePath = request.ImagePath.Trim();
        }
        await _db.SaveChangesAsync(cancellationToken); // assign tod.Id

        // Re-point the event to this pop's repop time + ToD. With no ToD there is no predicted
        // repop, so the board keeps whatever StartTime it had rather than drifting to a guess —
        // it simply closes, and an officer posts the next one by hand.
        if (repopUtc is { } nextPopUtc)
        {
            ev.StartTime = nextPopUtc;
        }
        ev.SourceTodId = tod.Id;
        ev.WindowAnchorAt = null; // camp over — next post re-anchors from the fresh StartTime

        // Honor the End Camp form's re-post choice: enable/disable (and set the lead of) this
        // monster's standing Repeat-on-ToD board so HnmRecurringBoardBackgroundService re-posts (or
        // doesn't) before the next pop. null = leave whatever's configured alone.
        if (request.Repost is { } repost)
        {
            await HnmRecurringBoardService.ApplyEndCampChoiceAsync(
                _db, ev, monster, repost, request.RepostLeadHours, cancellationToken);
        }

        // Auto-re-post lead (only when Repeat-on-ToD is on for this monster) — re-read after the
        // upsert so HnmRepostAt reflects the officer's just-made choice. Matched on every
        // spelling of the spawn, the same way the poller finds the board.
        var monsterNames = HnmConfig.MonsterMatchNamesLower(monster);
        var leadHours = await _db.HnmRecurringBoards
            .Where(b => b.LinkshellId == ev.LinkshellId
                && monsterNames.Contains(b.MonsterName.ToLower())
                && b.Enabled)
            .Select(b => (double?)b.LeadHours)
            .FirstOrDefaultAsync(cancellationToken);
        // No repop to count back from = nothing to schedule, so the board won't auto-re-post.
        ev.HnmRepostAt = repopUtc is { } repostAnchor && leadHours.HasValue
            ? repostAnchor.AddHours(-leadHours.Value)
            : null;

        var effectiveCount = DiscordEventMessageBuilder.EffectiveWindowCount(ev);
        // Default to the window the BOARD was showing when the officer hit End Camp (the awaited
        // window), not the raw already-opened counter. Check-in records WdArrivalWindow off the
        // same helper, so both ends of the credit range sit on one scale: total DKP is unchanged,
        // and every number the officer saw matches the one that gets stored. Computed BEFORE
        // NextWindowAt is cleared below — FocusWindow reads it.
        var popWindow = Math.Clamp(
            request.PopWindow ?? DiscordEventMessageBuilder.FocusWindow(ev), 1, effectiveCount);
        // Mirror it onto the ToD as well, so re-opening this pop on the ToDs tab shows the same
        // window End Camp recorded. Committed by the SaveChanges at the end of the method.
        tod.PopWindow = popWindow;
        ev.NextWindowAt = null; // popped — no more windows

        // Record the camp's outcome on the event before the handoff reads it.
        if (DiscordEventMessageBuilder.IsWd(ev))
        {
            ev.WdClaimed = claimed;
            ev.WdKilled = killed;
            ev.WdPopWindow = popWindow;
        }

        // BOTH modes now hand the camp off to the Event System page's attendance sections as a pending review row
        // instead of paying DKP here. An officer reviews it and their Post is what credits the
        // ledger (WindowEventDkpLedgerService). This is why Manual Check In no longer waits out an
        // "Awaiting Processing" grace: the review step the grace was standing in for is real now.
        //
        // Must run BEFORE the teardown below — the Standard roster reads AppUserEventWindow, which
        // cascades off AppUserEventId, so the presence data dies with the wipe. Stages into this
        // context; the single SaveChanges at the end commits the handoff and the wipe together.
        await _handoff.StageHandoffAsync(ev, popWindow, claimed, killed, cancellationToken);

        // The pop is done — tear the board down (both slot signups and no-slot attendees) and
        // enter the defeated / auto-re-post cycle. Deliberately do NOT stamp the recurring
        // board's LastSourceTodId: leaving it lets the poller re-post THIS board.
        //
        // Manual Check In camps used to be torn down later, by the background finalizer once the
        // grace elapsed; with no grace left they recycle immediately, exactly like Standard.
        // WdFinalizedAt is stamped here for the same reason — every Manual Check In surface gates on it being
        // null, and HnmEventSeeder.ClearWdCampState clears the block when the board is re-posted.
        ev.HnmDefeatedAt = nowUtc;
        if (DiscordEventMessageBuilder.IsWd(ev))
        {
            ev.WdFinalizedAt = nowUtc;
            ev.HnmWindowNumber = 1;
        }
        // Un-commence: the camp is no longer running, so it drops out of the live section and
        // back into the queue (its next StartTime is the future repop). Auto-start re-commences
        // it when that repop arrives / the recurring board reactivates it.
        ev.CommencementStartTime = null;
        var slotSignups = await _db.EventPartySlotSignups
            .Where(s => s.EventId == ev.Id)
            .ToListAsync(cancellationToken);
        _db.EventPartySlotSignups.RemoveRange(slotSignups);
        var noSlotAttendees = await _db.AppUserEvents
            .Where(p => p.EventId == ev.Id)
            .ToListAsync(cancellationToken);
        _db.AppUserEvents.RemoveRange(noSlotAttendees);
        // The board is RECYCLED, not deleted, so its EventAttendanceWindows would otherwise
        // outlive this camp with their attendee rows cascaded away — and the unique
        // (EventId, SequenceNumber) index would then hand the next camp's window-1 scan the
        // stale row from this one. Clear them so the re-posted board starts fresh.
        var scannedWindows = await _db.EventAttendanceWindows
            .Where(w => w.EventId == ev.Id)
            .ToListAsync(cancellationToken);
        _db.EventAttendanceWindows.RemoveRange(scannedWindows);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "HNM camp popped: event {EventId} ({Monster}) window {Window}, repop {Repop:o}, mode {Mode}.",
            ev.Id, monster, popWindow, repopUtc, DiscordEventMessageBuilder.IsWd(ev) ? "Manual Check In" : "Standard");

        return new Result(true, null, repopUtc, ev.HnmRepostAt);
    }

}
