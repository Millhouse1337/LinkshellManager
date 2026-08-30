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
    private readonly AdminOverrideService _adminOverride;
    private readonly TimeZoneConversionService _timeZones;
    private readonly EventHistoryEditService _editService;
    private readonly EventCommentService _comments;

    public EventHistoryController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        AdminOverrideService adminOverride,
        TimeZoneConversionService timeZones,
        EventHistoryEditService editService,
        EventCommentService comments)
    {
        _context = context;
        _userManager = userManager;
        _adminOverride = adminOverride;
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
        var (card, failure) = await BuildCardAsync(id, returnUrl: null);
        if (failure is not null)
        {
            return failure;
        }

        // The standalone page keeps its ViewBag contract; the card model is simply where those
        // values are computed now, so this page and the inline cards on the Event System page can
        // never disagree about what an officer may see or edit.
        ViewBag.CanReconcileActive = card!.CanManage;
        ViewBag.CanEditHistory = card.CanManage;
        ViewBag.CommentsCanManage = card.CanManage;
        ViewBag.ActivityTrackingEnabled = card.ActivityTrackingEnabled;
        ViewBag.DkpStep = card.DkpStep;
        ViewBag.Absentees = card.Absentees;
        ViewBag.WindowArchive = card.WindowArchive;
        ViewBag.Comments = card.Comments;
        ViewBag.CommentUserId = card.CommentUserId;
        return View(card.History);
    }

    // ONE expanded past-event card, fetched on demand by the Event System page's Past events
    // section so the list itself stays cheap — see PastEventCardViewModel. Membership-gated like
    // Details; the editors inside are officer-gated by CanManage, and every action they post to
    // re-authorizes independently.
    [HttpGet]
    public async Task<IActionResult> Card(int id, string? returnUrl = null)
    {
        var (card, failure) = await BuildCardAsync(id, returnUrl);
        return failure ?? PartialView("_CardBody", card!);
    }

    // Loads a closed event and everything a card (or the Details page) renders for it. The second
    // tuple slot carries the refusal — Challenge / NotFound / Forbid — when there's nothing to
    // build.
    private async Task<(PastEventCardViewModel? Card, IActionResult? Failure)> BuildCardAsync(int id, string? returnUrl)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return (null, Challenge());
        }

        var history = await _context.EventHistories
            .Include(item => item.AppUserEventHistories)
            .FirstOrDefaultAsync(item => item.Id == id);

        if (history is null)
        {
            return (null, NotFound());
        }

        var hasAccess = await _context.AppUserLinkshells
            .AnyAsync(link => link.AppUserId == user.Id && link.LinkshellId == history.LinkshellId);
        if (!hasAccess)
        {
            return (null, Forbid());
        }

        history.StartTime = ConvertUtcToUserTimeZone(history.StartTime, user.TimeZone);
        history.EndTime = ConvertUtcToUserTimeZone(history.EndTime, user.TimeZone);

        // Leaders/Officers can reconcile per-member active-status credit AND edit the closed
        // event's data (metadata / DKP / attendees).
        var canManage = await CanManageAsync(user.Id, history.LinkshellId);

        var linkshell = await _context.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == history.LinkshellId)
            .Select(l => new { l.EnableActivityTracking, l.DkpRoundingIncrement })
            .FirstOrDefaultAsync();

        // Full-roster participants: surface members who did NOT attend so they can be marked
        // Absent (default) or added to the event. An absence here feeds the activity streak
        // exactly like an uncredited attendee (no credited row = absence — see MemberActivityService).
        var attendeeUserIds = history.AppUserEventHistories
            .Where(p => p.AppUserId != null)
            .Select(p => p.AppUserId!)
            .ToHashSet(StringComparer.Ordinal);
        var roster = await _context.AppUserLinkshells
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == history.LinkshellId && m.AppUserId != null)
            .ToListAsync();

        // The camp's attendance windows, re-parented to this history at close. Empty for a timed
        // event, and for any windowed one closed before the archive existed — those windows were
        // cascaded away with the Event and are not recoverable. PostedAt is shifted into the
        // reader's zone here so no view has to know about time zones.
        var windowArchive = await EventHistoryWindowsReader.LoadAsync(_context, history, HttpContext.RequestAborted);
        windowArchive = windowArchive with
        {
            Windows = windowArchive.Windows
                .Select(window => window with
                {
                    PostedAt = ConvertUtcToUserTimeZone(window.PostedAt, user.TimeZone) ?? window.PostedAt
                })
                .ToList()
        };

        var card = new PastEventCardViewModel
        {
            History = history,
            CanManage = canManage,
            // The whole active-credit apparatus (tags, wording) only reflects tracking when the
            // linkshell has opted into it.
            ActivityTrackingEnabled = linkshell?.EnableActivityTracking ?? false,
            // Step the DKP editors by the linkshell's rounding increment (Quarter = 0.25 /
            // Half = 0.5) so the input matches the Discord Activity.
            DkpStep = DkpRounding.StepFor(linkshell?.DkpRoundingIncrement ?? default),
            Participants = history.AppUserEventHistories
                .OrderBy(p => p.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Absentees = roster
                .Where(m => !attendeeUserIds.Contains(m.AppUserId!))
                .OrderBy(m => m.CharacterName ?? m.AppUser!.CharacterName ?? m.AppUser!.UserName,
                    StringComparer.OrdinalIgnoreCase)
                .ToList(),
            WindowArchive = windowArchive,
            Comments = await _comments.ListAsync(history.Id, HttpContext.RequestAborted),
            CommentUserId = user.Id,
            ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null,
        };
        return (card, null);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddComment(int id, string? body, bool isAnonymous = false, string? returnUrl = null)
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
        return BackTo(returnUrl, id);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteComment(int id, int commentId, string? returnUrl = null)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var history = await _context.EventHistories.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id);
        if (history is null) return NotFound();
        var canManage = await CanManageAsync(user.Id, history.LinkshellId);
        await _comments.DeleteAsync(commentId, user.Id, canManage, HttpContext.RequestAborted);
        return BackTo(returnUrl, id);
    }

    // Reconcile active-status credit: leadership (un)checks which attendees earned
    // credit toward active-member status for this event. Posted "credited" carries
    // the checked AppUserEventHistory row ids; everything else is set uncredited.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveActiveCredits(int id, int[]? credited, string? returnUrl = null)
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
        return BackTo(returnUrl, id);
    }

    // Undo active-status credit for the ENTIRE event (every attendee) — for an
    // event credited by accident. Recomputes member statuses after.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearActiveCredits(int id, string? returnUrl = null)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;

        var changed = await _editService.SetAllParticipantsActiveCreditAsync(history!.Id, credited: false, HttpContext.RequestAborted);
        TempData["EventHistoryStatus"] = changed > 0
            ? $"Active-status credit removed for the whole event ({changed} attendee{(changed == 1 ? "" : "s")})."
            : "No attendees had active-status credit to remove.";
        return BackTo(returnUrl, id);
    }

    // Undo absences for the ENTIRE event — stop it counting toward active tracking
    // so members who missed it aren't marked absent for it. Recomputes statuses.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearAbsences(int id, string? returnUrl = null)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;

        var changed = await _editService.SetEventCountsTowardActiveAsync(history!.Id, counts: false, HttpContext.RequestAborted);
        TempData["EventHistoryStatus"] = changed
            ? "Absences undone — this event no longer counts toward active tracking."
            : "This event already doesn't count toward active tracking.";
        return BackTo(returnUrl, id);
    }

    // Edit a closed event's data. Changing DKP/hour rescales every attendee's
    // earned DKP (balance + lifetime), via EventHistoryEditService.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditDetails(
        int id, string? eventName, string? eventType, string? eventLocation,
        string? details, double? duration, int? dkpPerHour, string? returnUrl = null)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;

        await _editService.EditEventAsync(history!.Id,
            new EventHistoryEditInput(eventName, eventType, eventLocation, details, duration, dkpPerHour),
            HttpContext.RequestAborted);
        TempData["EventHistoryStatus"] = "Event details updated.";
        return BackTo(returnUrl, id);
    }

    // Set one attendee's earned DKP to a specific value.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetParticipantDkp(int id, int participantId, double amount, string? returnUrl = null)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;

        await _editService.SetParticipantDkpAsync(history!.Id, participantId, amount, HttpContext.RequestAborted);
        TempData["EventHistoryStatus"] = "Attendee DKP updated.";
        return BackTo(returnUrl, id);
    }

    // Remove an attendee from a closed event (refunds their earned DKP).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveParticipant(int id, int participantId, string? returnUrl = null)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;

        await _editService.RemoveParticipantAsync(history!.Id, participantId, HttpContext.RequestAborted);
        TempData["EventHistoryStatus"] = "Attendee removed and DKP refunded.";
        return BackTo(returnUrl, id);
    }

    // Add a member to a closed event after the fact and grant DKP — wired into the DKP
    // ledger + balance via EventHistoryEditService.AddParticipantAsync.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddParticipant(
        int id, string appUserId, double amount, string? jobType, string? jobName, string? subJobName,
        string? returnUrl = null)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;
        if (string.IsNullOrWhiteSpace(appUserId))
        {
            TempData["EventHistoryError"] = "Select a member to add.";
            return BackTo(returnUrl, id);
        }

        var ok = await _editService.AddParticipantAsync(
            history!.Id, appUserId, amount, jobType, jobName, subJobName, activeCredit: true, HttpContext.RequestAborted);
        TempData[ok ? "EventHistoryStatus" : "EventHistoryError"] = ok
            ? "Member added to the event and DKP granted."
            : "Couldn't add that member (already on the event, or not a member of the linkshell).";
        return BackTo(returnUrl, id);
    }

    // Delete a closed event outright — reverses every DKP move it made (earned + loot spent) and
    // takes its attendance, archived windows, loot and comments with it. The web twin of the
    // Activity card's Delete Event button; both go through EventHistoryEditService.DeleteEventAsync.
    //
    // Never returns to Details: the event it describes no longer exists. A card posts its own page
    // as returnUrl, and everything else lands on the history index.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEvent(int id, string? returnUrl = null)
    {
        var (history, forbid) = await LoadManageableAsync(id);
        if (forbid is not null) return forbid;

        var ok = await _editService.DeleteEventAsync(history!.Id, HttpContext.RequestAborted);
        TempData[ok ? "EventHistoryStatus" : "EventHistoryError"] = ok
            ? "Event deleted and its DKP reversed."
            : "That event no longer exists.";
        return Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl!) : RedirectToAction(nameof(Index));
    }

    // Where a mutation sends the browser when it wasn't submitted through fetch. The inline cards
    // on the Event System page post their own URL so a no-JS submit lands back on that page
    // (anchored at the card) instead of the standalone Details page. Local URLs only — anything
    // else here would be an open redirect.
    private IActionResult BackTo(string? returnUrl, int id)
        => Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl!)
            : RedirectToAction(nameof(Details), new { id });

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
        if (membership is null) return false;
        return LinkshellRanks.IsLeaderOrOfficer(membership.Rank)
               || await _adminOverride.IsActiveForAsync(appUserId, HttpContext.RequestAborted);
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
        => _timeZones.ToUserTime(utcDateTime, timeZoneId);
}
