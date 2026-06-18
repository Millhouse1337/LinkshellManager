using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class EventHistoryController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly TimeZoneConversionService _timeZones;
    private readonly EventHistoryEditService _editService;
    private readonly EventCommentService _comments;

    public EventHistoryController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        TimeZoneConversionService timeZones,
        EventHistoryEditService editService,
        EventCommentService comments)
    {
        _context = context;
        _userManager = userManager;
        _timeZones = timeZones;
        _editService = editService;
        _comments = comments;
    }
    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var linkshellIds = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.LinkshellId)
            .Distinct()
            .ToListAsync();

        var histories = await _context.EventHistories
            .Where(history =>
                linkshellIds.Contains(history.LinkshellId) &&
                (!user.PrimaryLinkshellId.HasValue || history.LinkshellId == user.PrimaryLinkshellId.Value))
            .OrderByDescending(history => history.EndTime ?? history.TimeStamp)
            .ToListAsync();

        foreach (var history in histories)
        {
            history.StartTime = ConvertUtcToUserTimeZone(history.StartTime, user.TimeZone);
            history.EndTime = ConvertUtcToUserTimeZone(history.EndTime, user.TimeZone);
        }

        return View(histories);
    }
    public async Task<IActionResult> Details(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var history = await _context.EventHistories
            .Include(item => item.AppUserEventHistories)
            .FirstOrDefaultAsync(item => item.Id == id);

        if (history is null)
        {
            return NotFound();
        }

        var hasAccess = await _context.AppUserLinkshells
            .AnyAsync(link => link.AppUserId == user.Id && link.LinkshellId == history.LinkshellId);
        if (!hasAccess)
        {
            return Forbid();
        }

        history.StartTime = ConvertUtcToUserTimeZone(history.StartTime, user.TimeZone);
        history.EndTime = ConvertUtcToUserTimeZone(history.EndTime, user.TimeZone);
        // Leaders/Officers can reconcile per-member active-status credit AND edit
        // the closed event's data (metadata / DKP / attendees) on this page.
        var canManage = await CanManageAsync(user.Id, history.LinkshellId);
        ViewBag.CanReconcileActive = canManage;
        ViewBag.CanEditHistory = canManage;
        // The whole active-credit apparatus (column, reconcile, tags) only shows
        // when the linkshell has opted into member activity tracking.
        ViewBag.ActivityTrackingEnabled = await _context.Linkshells
            .Where(l => l.Id == history.LinkshellId)
            .Select(l => l.EnableActivityTracking)
            .FirstOrDefaultAsync();

        // Step the participant-DKP editor by the linkshell's rounding increment
        // (Quarter = 0.25 / Half = 0.5) so the input matches the Discord Activity.
        var roundingIncrement = await _context.Linkshells
            .Where(l => l.Id == history.LinkshellId)
            .Select(l => l.DkpRoundingIncrement)
            .FirstOrDefaultAsync();
        ViewBag.DkpStep = DkpRounding.StepFor(roundingIncrement);

        // Full-roster participants: surface members who did NOT attend so they can be
        // marked Absent (default) or added to the event. The view renders attendees first,
        // then these absentees A–Z. An absence here feeds the activity streak exactly like
        // an uncredited attendee (no credited row = absence — see MemberActivityService).
        var attendeeUserIds = history.AppUserEventHistories
            .Where(p => p.AppUserId != null)
            .Select(p => p.AppUserId!)
            .ToHashSet(StringComparer.Ordinal);
        var roster = await _context.AppUserLinkshells
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == history.LinkshellId && m.AppUserId != null)
            .ToListAsync();
        ViewBag.Absentees = roster
            .Where(m => !attendeeUserIds.Contains(m.AppUserId!))
            .OrderBy(m => m.CharacterName ?? m.AppUser!.CharacterName ?? m.AppUser!.UserName,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
        ViewBag.MemberStreaks = await new MemberActivityService(_context)
            .ComputeStreaksByAppUserAsync(history.LinkshellId, HttpContext.RequestAborted);

        // Post-event discussion.
        ViewBag.Comments = await _comments.ListAsync(history.Id, HttpContext.RequestAborted);
        ViewBag.CommentUserId = user.Id;
        ViewBag.CommentsCanManage = canManage;
        return View(history);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddComment(int id, string? body, bool isAnonymous = false)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var history = await _context.EventHistories.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id);
        if (history is null) return NotFound();
        var membership = await _context.AppUserLinkshells.AsNoTracking()
            .FirstOrDefaultAsync(m => m.AppUserId == user.Id && m.LinkshellId == history.LinkshellId);
        if (membership is null) return Forbid();

        if (!string.IsNullOrWhiteSpace(body))
        {
            var character = membership.CharacterName ?? user.CharacterName;
            await _comments.AddAsync(id, user.Id, character, body, isAnonymous, HttpContext.RequestAborted);
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteComment(int id, int commentId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var history = await _context.EventHistories.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id);
        if (history is null) return NotFound();
        var canManage = await CanManageAsync(user.Id, history.LinkshellId);
        await _comments.DeleteAsync(commentId, user.Id, canManage, HttpContext.RequestAborted);
        return RedirectToAction(nameof(Details), new { id });
    }

    // Reconcile active-status credit: leadership (un)checks which attendees earned
    // credit toward active-member status for this event. Posted "credited" carries
    // the checked AppUserEventHistory row ids; everything else is set uncredited.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveActiveCredits(int id, int[]? credited)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var history = await _context.EventHistories
            .Include(item => item.AppUserEventHistories)
            .FirstOrDefaultAsync(item => item.Id == id);
        if (history is null)
        {
            return NotFound();
        }
        if (!await CanManageAsync(user.Id, history.LinkshellId))
        {
            return Forbid();
        }

        var creditedSet = (credited ?? Array.Empty<int>()).ToHashSet();
        foreach (var row in history.AppUserEventHistories)
        {
            row.ActiveCredit = creditedSet.Contains(row.Id);
        }
        await _context.SaveChangesAsync();

        // Credit changes alter the attendance streak → recompute member statuses.
        await new Services.MemberActivityService(_context).ApplyComputedStatusAsync(history.LinkshellId, HttpContext.RequestAborted);

        TempData["EventHistoryStatus"] = "Active-status credit updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Undo active-status credit for the ENTIRE event (every attendee) — for an
    // event credited by accident. Recomputes member statuses after.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearActiveCredits(int id)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;

        var changed = await _editService.SetAllParticipantsActiveCreditAsync(history!.Id, credited: false, HttpContext.RequestAborted);
        TempData["EventHistoryStatus"] = changed > 0
            ? $"Active-status credit removed for the whole event ({changed} attendee{(changed == 1 ? "" : "s")})."
            : "No attendees had active-status credit to remove.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Undo absences for the ENTIRE event — stop it counting toward active tracking
    // so members who missed it aren't marked absent for it. Recomputes statuses.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearAbsences(int id)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;

        var changed = await _editService.SetEventCountsTowardActiveAsync(history!.Id, counts: false, HttpContext.RequestAborted);
        TempData["EventHistoryStatus"] = changed
            ? "Absences undone — this event no longer counts toward active tracking."
            : "This event already doesn't count toward active tracking.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Edit a closed event's data. Changing DKP/hour rescales every attendee's
    // earned DKP (balance + lifetime), via EventHistoryEditService.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditDetails(
        int id, string? eventName, string? eventType, string? eventLocation,
        string? details, double? duration, int? dkpPerHour)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;

        await _editService.EditEventAsync(history!.Id,
            new EventHistoryEditInput(eventName, eventType, eventLocation, details, duration, dkpPerHour),
            HttpContext.RequestAborted);
        TempData["EventHistoryStatus"] = "Event details updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Set one attendee's earned DKP to a specific value.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetParticipantDkp(int id, int participantId, double amount)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;

        await _editService.SetParticipantDkpAsync(history!.Id, participantId, amount, HttpContext.RequestAborted);
        TempData["EventHistoryStatus"] = "Attendee DKP updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Remove an attendee from a closed event (refunds their earned DKP).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveParticipant(int id, int participantId)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;

        await _editService.RemoveParticipantAsync(history!.Id, participantId, HttpContext.RequestAborted);
        TempData["EventHistoryStatus"] = "Attendee removed and DKP refunded.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Add a member to a closed event after the fact and grant DKP — wired into the DKP
    // ledger + balance via EventHistoryEditService.AddParticipantAsync.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddParticipant(
        int id, string appUserId, double amount, string? jobType, string? jobName, string? subJobName)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;
        if (string.IsNullOrWhiteSpace(appUserId))
        {
            TempData["EventHistoryError"] = "Select a member to add.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var ok = await _editService.AddParticipantAsync(
            history!.Id, appUserId, amount, jobType, jobName, subJobName, activeCredit: true, HttpContext.RequestAborted);
        TempData[ok ? "EventHistoryStatus" : "EventHistoryError"] = ok
            ? "Member added to the event and DKP granted."
            : "Couldn't add that member (already on the event, or not a member of the linkshell).";
        return RedirectToAction(nameof(Details), new { id });
    }

    // Loads the history row and enforces leader/officer access. Returns the
    // forbidding result (Challenge/NotFound/Forbid) in the second tuple slot.
    private async Task<(EventHistory? History, IActionResult? Result)> LoadManageableAsync(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return (null, Challenge());

        var history = await _context.EventHistories.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id);
        if (history is null) return (null, NotFound());
        if (!await CanManageAsync(user.Id, history.LinkshellId)) return (null, Forbid());
        return (history, null);
    }

    private async Task<bool> CanManageAsync(string appUserId, int linkshellId)
    {
        var membership = await _context.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId);
        return membership is not null && LinkshellRanks.IsLeaderOrOfficer(membership.Rank);
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
        => _timeZones.ToUserTime(utcDateTime, timeZoneId);
}

