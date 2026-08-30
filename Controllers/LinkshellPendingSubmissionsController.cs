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
public sealed class LinkshellPendingSubmissionsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly AdminOverrideService _adminOverride;
    private readonly SubmissionApprovalService _approvals;
    private readonly MonsterTimingResolver _monsterTimings;

    public LinkshellPendingSubmissionsController(
        ApplicationDbContext db,
        UserManager<AppUser> userManager,
        AdminOverrideService adminOverride,
        SubmissionApprovalService approvals,
        MonsterTimingResolver monsterTimings)
    {
        _db = db;
        _userManager = userManager;
        _adminOverride = adminOverride;
        _approvals = approvals;
        _monsterTimings = monsterTimings;
    }

    [HttpGet("/linkshells/{linkshellId:int}/pending-submissions")]
    public async Task<IActionResult> Index(int linkshellId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var role = await GetEffectiveRoleAsync(user.Id, linkshellId);
        var canReviewTods = role?.CanManageTods == true;
        var canReviewAttendance = role?.CanModerateLiveEvent == true;
        if (!canReviewTods && !canReviewAttendance) return Forbid();

        var linkshell = await _db.Linkshells.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkshellId);
        if (linkshell is null) return NotFound();

        var rows = new List<PendingSubmissionRow>();

        if (canReviewTods)
        {
            var tods = await _db.PendingTodSubmissions
                .AsNoTracking()
                .Where(s => s.LinkshellId == linkshellId)
                .Include(s => s.SubmittedBy)
                .OrderBy(s => s.SubmittedAtUtc)
                .ToListAsync();
            foreach (var t in tods)
            {
                rows.Add(new PendingSubmissionRow
                {
                    Id = t.Id,
                    Type = "Tod",
                    SubmittedByDisplay = DisplayName(t.SubmittedBy),
                    SubmittedAtUtc = t.SubmittedAtUtc,
                    Summary = $"ToD: {t.MonsterName ?? "?"} @ {(t.Time.HasValue ? t.Time.Value.ToString("yyyy-MM-dd HH:mm") + " UTC" : "no time")}",
                });
            }
        }

        if (canReviewAttendance)
        {
            var windows = await _db.PendingAttendanceWindowSubmissions
                .AsNoTracking()
                .Where(s => s.LinkshellId == linkshellId)
                .Include(s => s.SubmittedBy)
                .Include(s => s.Event)
                .OrderBy(s => s.SubmittedAtUtc)
                .ToListAsync();
            foreach (var w in windows)
            {
                rows.Add(new PendingSubmissionRow
                {
                    Id = w.Id,
                    Type = "AttendanceWindow",
                    SubmittedByDisplay = DisplayName(w.SubmittedBy),
                    SubmittedAtUtc = w.SubmittedAtUtc,
                    Summary = $"Window {w.WindowIndex} attendance for {w.Event?.EventName ?? "event #" + w.EventId}",
                });
            }

            var snapshots = await _db.PendingAttendanceSnapshotSubmissions
                .AsNoTracking()
                .Where(s => s.LinkshellId == linkshellId)
                .Include(s => s.SubmittedBy)
                .OrderBy(s => s.SubmittedAtUtc)
                .ToListAsync();
            foreach (var s in snapshots)
            {
                rows.Add(new PendingSubmissionRow
                {
                    Id = s.Id,
                    Type = "AttendanceSnapshot",
                    SubmittedByDisplay = DisplayName(s.SubmittedBy),
                    SubmittedAtUtc = s.SubmittedAtUtc,
                    Summary = $"Snapshot: {s.EntryCount} members by {s.CapturedByCharacterName ?? "?"}",
                });
            }
        }

        return View(new LinkshellPendingSubmissionsViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            Rows = rows.OrderBy(r => r.SubmittedAtUtc).ToList(),
            CanReviewTods = canReviewTods,
            CanReviewAttendance = canReviewAttendance,
        });
    }

    // ---------- ToD edit page ----------

    [HttpGet("/linkshells/{linkshellId:int}/pending-submissions/tod/{submissionId:int}")]
    public async Task<IActionResult> EditTod(int linkshellId, int submissionId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var role = await GetEffectiveRoleAsync(user.Id, linkshellId);
        if (role?.CanManageTods != true) return Forbid();

        var submission = await _db.PendingTodSubmissions
            .AsNoTracking()
            .Include(s => s.LootRows)
            .Include(s => s.SubmittedBy)
            .Include(s => s.Linkshell)
            .FirstOrDefaultAsync(s => s.Id == submissionId && s.LinkshellId == linkshellId);
        if (submission is null) return NotFound();

        var members = await _db.AppUserLinkshells
            .AsNoTracking()
            .Where(m => m.LinkshellId == linkshellId && m.CharacterName != null)
            .Select(m => m.CharacterName!)
            .OrderBy(n => n)
            .ToListAsync();

        return View(new EditPendingTodViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = submission.Linkshell?.LinkshellName,
            SubmissionId = submission.Id,
            SubmittedByDisplay = DisplayName(submission.SubmittedBy),
            SubmittedAtUtc = submission.SubmittedAtUtc,
            MonsterName = submission.MonsterName,
            DayNumber = submission.DayNumber,
            Claim = submission.Claim,
            Time = submission.Time,
            Cooldown = submission.Cooldown,
            Interval = submission.Interval,
            RepopTime = submission.RepopTime,
            ImagePath = submission.ImagePath,
            LootRows = submission.LootRows
                .OrderBy(r => r.Id)
                .Select(r => new EditPendingTodLootRow { ItemName = r.ItemName, ItemWinner = r.ItemWinner, WinningDkpSpent = r.WinningDkpSpent })
                .ToList(),
            MonsterOptions = (await _monsterTimings.GetMapAsync(linkshellId, HttpContext.RequestAborted)).EventMonsterOptions,
            CooldownOptions = TodManagerViewModel.SupportedCooldowns,
            IntervalOptions = TodManagerViewModel.SupportedIntervals,
            LinkshellMembers = members,
        });
    }

    [HttpPost("/linkshells/{linkshellId:int}/pending-submissions/tod/{submissionId:int}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveTod(
        int linkshellId,
        int submissionId,
        EditPendingTodViewModel form,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var role = await GetEffectiveRoleAsync(user.Id, linkshellId);
        if (role?.CanManageTods != true) return Forbid();

        var submission = await _db.PendingTodSubmissions
            .Include(s => s.LootRows)
            .FirstOrDefaultAsync(s => s.Id == submissionId && s.LinkshellId == linkshellId, cancellationToken);
        if (submission is null) return NotFound();

        // Apply officer edits to the pending row before approve materializes it.
        submission.MonsterName = string.IsNullOrWhiteSpace(form.MonsterName) ? submission.MonsterName : form.MonsterName.Trim();
        submission.DayNumber = form.DayNumber;
        submission.Claim = form.Claim;
        submission.Time = form.Time;
        submission.Cooldown = string.IsNullOrWhiteSpace(form.Cooldown) ? submission.Cooldown : form.Cooldown.Trim();
        submission.Interval = string.IsNullOrWhiteSpace(form.Interval) ? submission.Interval : form.Interval.Trim();
        submission.RepopTime = form.RepopTime;

        // Replace loot rows wholesale with the edited list so toggled-off rows
        // disappear before materialization.
        _db.PendingTodLootSubmissions.RemoveRange(submission.LootRows);
        foreach (var lr in form.LootRows ?? new List<EditPendingTodLootRow>())
        {
            if (string.IsNullOrWhiteSpace(lr.ItemName) && string.IsNullOrWhiteSpace(lr.ItemWinner) && !lr.WinningDkpSpent.HasValue) continue;
            submission.LootRows.Add(new PendingTodLootSubmission
            {
                ItemName = lr.ItemName?.Trim(),
                ItemWinner = lr.ItemWinner?.Trim(),
                WinningDkpSpent = lr.WinningDkpSpent,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);

        var result = await _approvals.ApproveTodAsync(submissionId, cancellationToken);
        if (result == ApprovalResult.NotFound) return NotFound();
        if (result == ApprovalResult.InsufficientDkp)
        {
            TempData["PendingApprovalMessage"] =
                "Approval blocked: a loot winner doesn't have enough DKP for their item. "
                + "Adjust their DKP or edit the submission's loot, then approve again.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }
        TempData["PendingApprovalMessage"] = "ToD submission approved.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/pending-submissions/tod/{submissionId:int}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectTod(int linkshellId, int submissionId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var role = await GetEffectiveRoleAsync(user.Id, linkshellId);
        if (role?.CanManageTods != true) return Forbid();

        var result = await _approvals.RejectTodAsync(submissionId, null, cancellationToken);
        if (result == ApprovalResult.NotFound) return NotFound();
        TempData["PendingApprovalMessage"] = "ToD submission rejected and removed.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // ---------- Attendance Window edit page ----------

    [HttpGet("/linkshells/{linkshellId:int}/pending-submissions/attendance-window/{submissionId:int}")]
    public async Task<IActionResult> EditAttendanceWindow(int linkshellId, int submissionId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var role = await GetEffectiveRoleAsync(user.Id, linkshellId);
        if (role?.CanModerateLiveEvent != true) return Forbid();

        var submission = await _db.PendingAttendanceWindowSubmissions
            .AsNoTracking()
            .Include(s => s.Members)
            .Include(s => s.Event)
            .Include(s => s.SubmittedBy)
            .Include(s => s.Linkshell)
            .FirstOrDefaultAsync(s => s.Id == submissionId && s.LinkshellId == linkshellId);
        if (submission is null) return NotFound();

        return View(new EditPendingAttendanceWindowViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = submission.Linkshell?.LinkshellName,
            SubmissionId = submission.Id,
            SubmittedByDisplay = DisplayName(submission.SubmittedBy),
            SubmittedAtUtc = submission.SubmittedAtUtc,
            EventId = submission.EventId,
            EventName = submission.Event?.EventName,
            WindowIndex = submission.WindowIndex,
            Members = submission.Members
                .OrderBy(m => m.CharacterName)
                .Select(m => new EditPendingAttendanceMember
                {
                    Id = m.Id,
                    CharacterName = m.CharacterName,
                    MainJob = m.MainJob,
                    MainJobLevel = m.MainJobLevel,
                    SubJob = m.SubJob,
                    SubJobLevel = m.SubJobLevel,
                    Include = true,
                })
                .ToList(),
        });
    }

    [HttpPost("/linkshells/{linkshellId:int}/pending-submissions/attendance-window/{submissionId:int}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveAttendanceWindow(
        int linkshellId,
        int submissionId,
        EditPendingAttendanceWindowViewModel form,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var role = await GetEffectiveRoleAsync(user.Id, linkshellId);
        if (role?.CanModerateLiveEvent != true) return Forbid();

        var submission = await _db.PendingAttendanceWindowSubmissions
            .Include(s => s.Members)
            .FirstOrDefaultAsync(s => s.Id == submissionId && s.LinkshellId == linkshellId, cancellationToken);
        if (submission is null) return NotFound();

        // Apply officer toggles: drop any members the officer un-checked.
        var keep = (form.Members ?? new List<EditPendingAttendanceMember>())
            .Where(m => m.Include)
            .Select(m => m.Id)
            .ToHashSet();
        var toRemove = submission.Members.Where(m => !keep.Contains(m.Id)).ToList();
        _db.PendingAttendanceWindowMemberSubmissions.RemoveRange(toRemove);
        await _db.SaveChangesAsync(cancellationToken);

        var result = await _approvals.ApproveAttendanceWindowAsync(submissionId, cancellationToken);
        if (result == ApprovalResult.NotFound) return NotFound();
        TempData["PendingApprovalMessage"] = "Attendance window approved.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/pending-submissions/attendance-window/{submissionId:int}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectAttendanceWindow(int linkshellId, int submissionId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var role = await GetEffectiveRoleAsync(user.Id, linkshellId);
        if (role?.CanModerateLiveEvent != true) return Forbid();

        var result = await _approvals.RejectAttendanceWindowAsync(submissionId, null, cancellationToken);
        if (result == ApprovalResult.NotFound) return NotFound();
        TempData["PendingApprovalMessage"] = "Attendance window rejected.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // ---------- Attendance Snapshot edit page ----------

    [HttpGet("/linkshells/{linkshellId:int}/pending-submissions/attendance-snapshot/{submissionId:int}")]
    public async Task<IActionResult> EditAttendanceSnapshot(int linkshellId, int submissionId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var role = await GetEffectiveRoleAsync(user.Id, linkshellId);
        if (role?.CanModerateLiveEvent != true) return Forbid();

        var submission = await _db.PendingAttendanceSnapshotSubmissions
            .AsNoTracking()
            .Include(s => s.Entries)
            .Include(s => s.SubmittedBy)
            .Include(s => s.Linkshell)
            .FirstOrDefaultAsync(s => s.Id == submissionId && s.LinkshellId == linkshellId);
        if (submission is null) return NotFound();

        return View(new EditPendingAttendanceSnapshotViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = submission.Linkshell?.LinkshellName,
            SubmissionId = submission.Id,
            SubmittedByDisplay = DisplayName(submission.SubmittedBy),
            SubmittedAtUtc = submission.SubmittedAtUtc,
            CapturedAtUtc = submission.CapturedAtUtc,
            CapturedByCharacterName = submission.CapturedByCharacterName,
            UtcOffset = submission.UtcOffset,
            Entries = submission.Entries
                .OrderBy(e => e.CharacterName)
                .Select(e => new EditPendingSnapshotEntry
                {
                    Id = e.Id,
                    CharacterName = e.CharacterName,
                    MainJob = e.MainJob,
                    MainJobLevel = e.MainJobLevel,
                    SubJob = e.SubJob,
                    SubJobLevel = e.SubJobLevel,
                    Zone = e.Zone,
                    Include = true,
                })
                .ToList(),
        });
    }

    [HttpPost("/linkshells/{linkshellId:int}/pending-submissions/attendance-snapshot/{submissionId:int}/approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveAttendanceSnapshot(
        int linkshellId,
        int submissionId,
        EditPendingAttendanceSnapshotViewModel form,
        CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var role = await GetEffectiveRoleAsync(user.Id, linkshellId);
        if (role?.CanModerateLiveEvent != true) return Forbid();

        var submission = await _db.PendingAttendanceSnapshotSubmissions
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == submissionId && s.LinkshellId == linkshellId, cancellationToken);
        if (submission is null) return NotFound();

        var keep = (form.Entries ?? new List<EditPendingSnapshotEntry>())
            .Where(e => e.Include)
            .Select(e => e.Id)
            .ToHashSet();
        var toRemove = submission.Entries.Where(e => !keep.Contains(e.Id)).ToList();
        _db.PendingAttendanceSnapshotEntries.RemoveRange(toRemove);
        submission.EntryCount = submission.Entries.Count - toRemove.Count;
        await _db.SaveChangesAsync(cancellationToken);

        var result = await _approvals.ApproveSnapshotAsync(submissionId, cancellationToken);
        if (result == ApprovalResult.NotFound) return NotFound();
        TempData["PendingApprovalMessage"] = "Attendance snapshot approved.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/pending-submissions/attendance-snapshot/{submissionId:int}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectAttendanceSnapshot(int linkshellId, int submissionId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var role = await GetEffectiveRoleAsync(user.Id, linkshellId);
        if (role?.CanModerateLiveEvent != true) return Forbid();

        var result = await _approvals.RejectSnapshotAsync(submissionId, null, cancellationToken);
        if (result == ApprovalResult.NotFound) return NotFound();
        TempData["PendingApprovalMessage"] = "Attendance snapshot rejected.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    // ---------- Helpers ----------

    private async Task<LinkshellRole?> GetEffectiveRoleAsync(string appUserId, int linkshellId)
    {
        // The membership ROW, not just the rank string: a null rank and a missing
        // membership are otherwise indistinguishable, and the override below must
        // only ever fire for an actual member.
        var membership = await _db.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.AppUserId == appUserId && m.LinkshellId == linkshellId);
        if (membership is null) return null;

        if (await _adminOverride.IsActiveForAsync(appUserId, HttpContext.RequestAborted))
        {
            return LinkshellRoleDefaults.BuildFullAccessRole(linkshellId);
        }

        var rank = membership.Rank;
        if (rank is null) return null;
        var rankName = string.IsNullOrWhiteSpace(rank) ? "Member" : rank.Trim();
        return await _db.LinkshellRoles
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.LinkshellId == linkshellId && r.Name == rankName);
    }

    private static string DisplayName(AppUser? user)
    {
        if (user is null) return "(unknown)";
        return user.CharacterName ?? user.UserName ?? user.Id;
    }
}
