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
        if (role?.CanManageTods != true) return Forbid();

        var linkshell = await _db.Linkshells.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkshellId);
        if (linkshell is null) return NotFound();

        var rows = new List<PendingSubmissionRow>();
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

        return View(new LinkshellPendingSubmissionsViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            Rows = rows.OrderBy(r => r.SubmittedAtUtc).ToList(),
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
            CooldownOptions = TodCatalog.SupportedCooldowns,
            IntervalOptions = TodCatalog.SupportedIntervals,
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

        // Apply officer edits to the pending row before approve materializes it.
        var edits = new PendingTodEdits(
            form.MonsterName, form.DayNumber, form.Claim, form.Time, form.Cooldown, form.Interval, form.RepopTime,
            (form.LootRows ?? new List<EditPendingTodLootRow>())
                .Select(lr => new TodSubmissionLootInput(lr.ItemName, lr.ItemWinner, lr.WinningDkpSpent))
                .ToList());
        var result = await _approvals.ApproveTodWithEditsAsync(linkshellId, submissionId, edits, cancellationToken);
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

        var result = await _approvals.RejectTodAsync(linkshellId, submissionId, null, cancellationToken);
        if (result == ApprovalResult.NotFound) return NotFound();
        TempData["PendingApprovalMessage"] = "ToD submission rejected and removed.";
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
